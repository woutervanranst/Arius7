using Arius.Core.Encryption;
using Arius.Core.FileTree;
using Shouldly;

namespace Arius.Core.Tests.FileTree;

// ── 5.1 / 5.2  Model and serialization roundtrip ─────────────────────────────

public class TreeBlobSerializerTests
{
    private static readonly DateTimeOffset s_created  = new(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_modified = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly PlaintextPassthroughService s_enc = new();

    private static TreeBlob MakeBlob(params (string name, TreeEntryType type, string hash)[] items) =>
        new()
        {
            Entries = items.Select(i => new TreeEntry
            {
                Name     = i.name,
                Type     = i.type,
                Hash     = i.hash,
                Created  = i.type == TreeEntryType.File ? s_created  : null,
                Modified = i.type == TreeEntryType.File ? s_modified : null
            }).ToList()
        };

    [Test]
    public async Task Serialize_ThenDeserialize_RoundTrips()
    {
        var blob = MakeBlob(
            ("photo.jpg", TreeEntryType.File, "a1b2c3d4"),
            ("subdir/",   TreeEntryType.Dir,  "e5f6a7b8"));

        var bytes = await TreeBlobSerializer.SerializeForStorageAsync(blob, s_enc);
        var back  = await TreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Entries.Count.ShouldBe(2);

        var file = back.Entries.Single(e => e.Name == "photo.jpg");
        file.Type.ShouldBe(TreeEntryType.File);
        file.Hash.ShouldBe("a1b2c3d4");
        file.Created.ShouldNotBeNull();
        file.Modified.ShouldNotBeNull();

        var dir = back.Entries.Single(e => e.Name == "subdir/");
        dir.Type.ShouldBe(TreeEntryType.Dir);
        dir.Hash.ShouldBe("e5f6a7b8");
        dir.Created.ShouldBeNull();
        dir.Modified.ShouldBeNull();
    }

    [Test]
    public async Task Serialize_SortsEntriesByName()
    {
        // Insert entries in reverse order
        var blob = MakeBlob(
            ("z_last.txt",  TreeEntryType.File, "hash3"),
            ("a_first.txt", TreeEntryType.File, "hash1"),
            ("m_mid.txt",   TreeEntryType.File, "hash2"));

        var bytes = await TreeBlobSerializer.SerializeForStorageAsync(blob, s_enc);
        var back  = await TreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Entries[0].Name.ShouldBe("a_first.txt");
        back.Entries[1].Name.ShouldBe("m_mid.txt");
        back.Entries[2].Name.ShouldBe("z_last.txt");
    }

    [Test]
    public void Serialize_IsDeterministic_SameInputSameOutput()
    {
        var blob1 = MakeBlob(("b.jpg", TreeEntryType.File, "hash2"), ("a.jpg", TreeEntryType.File, "hash1"));
        var blob2 = MakeBlob(("a.jpg", TreeEntryType.File, "hash1"), ("b.jpg", TreeEntryType.File, "hash2"));

        var bytes1 = TreeBlobSerializer.Serialize(blob1);
        var bytes2 = TreeBlobSerializer.Serialize(blob2);

        bytes1.ShouldBe(bytes2);
    }

    [Test]
    public void Serialize_DirEntry_HasNoTimestamps_FileEntry_HasTimestamps()
    {
        var blob = new TreeBlob
        {
            Entries =
            [
                new TreeEntry { Name = "sub/", Type = TreeEntryType.Dir,  Hash = "abc", Created = null, Modified = null },
                new TreeEntry { Name = "f.txt", Type = TreeEntryType.File, Hash = "def", Created = s_created, Modified = s_modified }
            ]
        };

        var text = System.Text.Encoding.UTF8.GetString(TreeBlobSerializer.Serialize(blob));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Dir line: <hash> D <name>  — 3 space-separated tokens
        var dirLine = lines.Single(l => l.Contains(" D "));
        dirLine.Split(' ').Length.ShouldBe(3);

        // File line: <hash> F <created> <modified> <name>  — 5 space-separated tokens
        var fileLine = lines.Single(l => l.Contains(" F "));
        fileLine.Split(' ').Length.ShouldBe(5);
    }

    [Test]
    public async Task Serialize_FileEntryWithSpacesInName_RoundTrips()
    {
        var blob = MakeBlob(("my vacation photo.jpg", TreeEntryType.File, "abc123"));

        var bytes = await TreeBlobSerializer.SerializeForStorageAsync(blob, s_enc);
        var back  = await TreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Entries.Single().Name.ShouldBe("my vacation photo.jpg");
    }

    [Test]
    public async Task Serialize_DirEntryWithSpacesInName_RoundTrips()
    {
        var blob = MakeBlob(("2024 trip/", TreeEntryType.Dir, "def456"));

        var bytes = await TreeBlobSerializer.SerializeForStorageAsync(blob, s_enc);
        var back  = await TreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), s_enc);

        back.Entries.Single().Name.ShouldBe("2024 trip/");
    }

    // ── 5.3 Tree hash computation ─────────────────────────────────────────────

    [Test]
    public void ComputeHash_Deterministic_SameInputSameHash()
    {
        var enc  = new PlaintextPassthroughService();
        var blob = MakeBlob(("file.txt", TreeEntryType.File, "deadbeef"));

        var h1 = TreeBlobSerializer.ComputeHash(blob, enc);
        var h2 = TreeBlobSerializer.ComputeHash(blob, enc);

        h1.ShouldBe(h2);
        h1.Length.ShouldBe(64); // SHA256 hex
    }

    [Test]
    public void ComputeHash_MetadataChange_ProducesNewHash()
    {
        var enc  = new PlaintextPassthroughService();
        var blob1 = new TreeBlob
        {
            Entries =
            [
                new TreeEntry
                {
                    Name     = "file.txt",
                    Type     = TreeEntryType.File,
                    Hash     = "deadbeef",
                    Created  = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    Modified = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
                }
            ]
        };
        var blob2 = blob1 with
        {
            Entries =
            [
                blob1.Entries[0] with
                {
                    Modified = new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero) // different modified
                }
            ]
        };

        var h1 = TreeBlobSerializer.ComputeHash(blob1, enc);
        var h2 = TreeBlobSerializer.ComputeHash(blob2, enc);

        h1.ShouldNotBe(h2);
    }

    [Test]
    public void ComputeHash_WithPassphrase_DifferentFromPlaintext()
    {
        var blobPlain   = MakeBlob(("file.txt", TreeEntryType.File, "abc"));
        var plain       = new PlaintextPassthroughService();
        var withPass    = new PassphraseEncryptionService("secret");

        var h1 = TreeBlobSerializer.ComputeHash(blobPlain, plain);
        var h2 = TreeBlobSerializer.ComputeHash(blobPlain, withPass);

        h1.ShouldNotBe(h2);
    }
}

// ── 5.1 / 5.2 / 5.3  Storage serialization (gzip + optional encryption) ─────────

public class TreeBlobSerializerStorageTests
{
    private static readonly DateTimeOffset s_created  = new(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_modified = new(2024, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static TreeBlob MakeBlob() => new()
    {
        Entries =
        [
            new TreeEntry { Name = "photo.jpg", Type = TreeEntryType.File, Hash = "a1b2c3d4", Created = s_created, Modified = s_modified },
            new TreeEntry { Name = "subdir/",   Type = TreeEntryType.Dir,  Hash = "e5f6a7b8" }
        ]
    };

    [Test]
    public async Task SerializeForStorage_WithPassphrase_ThenDeserialize_RoundTrips()
    {
        var enc  = new PassphraseEncryptionService("test-passphrase");
        var blob = MakeBlob();

        var bytes  = await TreeBlobSerializer.SerializeForStorageAsync(blob, enc);
        var back   = await TreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), enc);

        back.Entries.Count.ShouldBe(2);
        back.Entries.Single(e => e.Name == "photo.jpg").Hash.ShouldBe("a1b2c3d4");
        back.Entries.Single(e => e.Name == "subdir/").Type.ShouldBe(TreeEntryType.Dir);
    }

    [Test]
    public async Task SerializeForStorage_WithPlaintext_ThenDeserialize_RoundTrips()
    {
        var enc  = new PlaintextPassthroughService();
        var blob = MakeBlob();

        var bytes = await TreeBlobSerializer.SerializeForStorageAsync(blob, enc);
        var back  = await TreeBlobSerializer.DeserializeFromStorageAsync(new MemoryStream(bytes), enc);

        back.Entries.Count.ShouldBe(2);
        back.Entries.Single(e => e.Name == "photo.jpg").Hash.ShouldBe("a1b2c3d4");
        back.Entries.Single(e => e.Name == "subdir/").Type.ShouldBe(TreeEntryType.Dir);
    }

    [Test]
    public async Task SerializeForStorage_WithPassphrase_StartsWithArGcm1Magic()
    {
        var enc   = new PassphraseEncryptionService("test-passphrase");
        var blob  = MakeBlob();

        var bytes = await TreeBlobSerializer.SerializeForStorageAsync(blob, enc);

        // AES-256-GCM ArGCM1 format: first 6 bytes are "ArGCM1"
        var prefix = System.Text.Encoding.ASCII.GetString(bytes[..6]);
        prefix.ShouldBe("ArGCM1");
    }

    [Test]
    public async Task SerializeForStorage_WithPassphrase_OutputIsNotPlaintext()
    {
        var enc  = new PassphraseEncryptionService("test-passphrase");
        var blob = MakeBlob();

        var bytes = await TreeBlobSerializer.SerializeForStorageAsync(blob, enc);
        var text  = System.Text.Encoding.UTF8.GetString(bytes);

        // Encrypted output should not contain file names in plaintext
        text.ShouldNotContain("photo.jpg");
        text.ShouldNotContain("subdir/");
    }
}

// ── 5.4 Manifest writer ────────────────────────────────────────────────────────

public class ManifestWriterTests
{
    [Test]
    public async Task Append_ThenRead_ContainsAllEntries()
    {
        var path = Path.GetTempFileName();
        try
        {
            await using var writer = new ManifestWriter(path);

            var e1 = new ManifestEntry("photos/a.jpg",   "hash1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var e2 = new ManifestEntry("docs/report.pdf","hash2", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            await writer.AppendAsync(e1);
            await writer.AppendAsync(e2);
        }
        finally
        {
            // reader after dispose
        }

        var lines = await File.ReadAllLinesAsync(path);
        var entries = lines.Where(l => !string.IsNullOrWhiteSpace(l))
                           .Select(ManifestEntry.Parse)
                           .ToList();

        entries.Count.ShouldBe(2);
        entries.Any(e => e.Path == "photos/a.jpg" && e.ContentHash == "hash1").ShouldBeTrue();
        entries.Any(e => e.Path == "docs/report.pdf").ShouldBeTrue();

        File.Delete(path);
    }

    [Test]
    public void ManifestEntry_Serialize_ThenParse_RoundTrips()
    {
        var now   = new DateTimeOffset(2024, 6, 15, 10, 30, 0, TimeSpan.Zero);
        var entry = new ManifestEntry("path/to/file.txt", "abcdef12", now, now.AddHours(1));
        var back  = ManifestEntry.Parse(entry.Serialize());

        back.Path.ShouldBe(entry.Path);
        back.ContentHash.ShouldBe(entry.ContentHash);
        back.Created.ShouldBe(entry.Created);
        back.Modified.ShouldBe(entry.Modified);
    }
}

// ── 5.5 External sort ─────────────────────────────────────────────────────────

public class ManifestSorterTests
{
    [Test]
    public async Task SortAsync_SortsEntriesByPath_Ordinal()
    {
        var path = Path.GetTempFileName();
        try
        {
            var now     = DateTimeOffset.UtcNow;
            var entries = new[]
            {
                new ManifestEntry("z/file.txt",   "h3", now, now),
                new ManifestEntry("a/file.txt",   "h1", now, now),
                new ManifestEntry("m/file.txt",   "h2", now, now),
            };

            await File.WriteAllLinesAsync(path, entries.Select(e => e.Serialize()));

            await ManifestSorter.SortAsync(path);

            var lines   = await File.ReadAllLinesAsync(path);
            var sorted  = lines.Where(l => !string.IsNullOrWhiteSpace(l))
                               .Select(ManifestEntry.Parse)
                               .ToList();

            sorted[0].Path.ShouldBe("a/file.txt");
            sorted[1].Path.ShouldBe("m/file.txt");
            sorted[2].Path.ShouldBe("z/file.txt");
        }
        finally
        {
            File.Delete(path);
        }
    }
}

// ── 5.8 Empty directory skipping (via TreeBuilder) ────────────────────────────

public class TreeBuilderTests
{
    private static readonly PlaintextPassthroughService s_enc = new();

    // Minimal stub IBlobContainerService that records uploads
    private sealed class FakeBlobService : Storage.IBlobContainerService
    {
        public readonly HashSet<string> Uploaded = new();
        public readonly HashSet<string> HeadChecked = new();

        public Task CreateContainerIfNotExistsAsync(CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata,
            Storage.BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken ct = default)
        {
            Uploaded.Add(blobName);
            return Task.CompletedTask;
        }

        public Task<Stream> DownloadAsync(string blobName, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<Storage.BlobMetadata> GetMetadataAsync(string blobName, CancellationToken ct = default)
        {
            HeadChecked.Add(blobName);
            return Task.FromResult(new Storage.BlobMetadata { Exists = false });
        }

        public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct = default) =>
            AsyncEnumerable.Empty<string>();

        public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SetTierAsync(string blobName, Storage.BlobTier tier, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task CopyAsync(string src, string dst, Storage.BlobTier tier, Storage.RehydratePriority? priority = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(string blobName, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null,
            CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream());
    }

    [Test]
    public async Task BuildAsync_EmptyManifest_ReturnsNull()
    {
        var manifestPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(manifestPath, "");

            var blobs   = new FakeBlobService();
            var builder = new TreeBuilder(blobs, s_enc, "account", "container");
            var root    = await builder.BuildAsync(manifestPath);

            root.ShouldBeNull();
            blobs.Uploaded.ShouldBeEmpty();
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    [Test]
    public async Task BuildAsync_SingleFile_RootTreeUploaded()
    {
        const string acct = "acct-single";
        const string cont = "cont-single";
        var cacheDir = TreeBuilder.GetDiskCacheDirectory(acct, cont);

        // Clean any stale disk cache from prior runs before the test
        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);

        var manifestPath = Path.GetTempFileName();
        try
        {
            var now   = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var entry = new ManifestEntry("readme.txt", "aabbccdd", now, now);
            await File.WriteAllTextAsync(manifestPath, entry.Serialize() + "\n");

            var blobs   = new FakeBlobService();
            var builder = new TreeBuilder(blobs, s_enc, acct, cont);
            var root    = await builder.BuildAsync(manifestPath);

            root.ShouldNotBeNull();
            root!.Length.ShouldBe(64);
            blobs.Uploaded.Count.ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            File.Delete(manifestPath);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task BuildAsync_IdenticalManifest_SameRootHash()
    {
        const string acct1 = "acc-identical-1", cont1 = "con-identical-1";
        const string acct2 = "acc-identical-2", cont2 = "con-identical-2";
        var cache1 = TreeBuilder.GetDiskCacheDirectory(acct1, cont1);
        var cache2 = TreeBuilder.GetDiskCacheDirectory(acct2, cont2);
        if (Directory.Exists(cache1)) Directory.Delete(cache1, recursive: true);
        if (Directory.Exists(cache2)) Directory.Delete(cache2, recursive: true);

        var manifestPath1 = Path.GetTempFileName();
        var manifestPath2 = Path.GetTempFileName();
        try
        {
            var now   = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var lines = new[]
            {
                new ManifestEntry("photos/a.jpg", "hash1", now, now).Serialize(),
                new ManifestEntry("photos/b.jpg", "hash2", now, now).Serialize(),
                new ManifestEntry("docs/r.pdf",   "hash3", now, now).Serialize(),
            };
            var content = string.Join("\n", lines) + "\n";
            await File.WriteAllTextAsync(manifestPath1, content);
            await File.WriteAllTextAsync(manifestPath2, content);

            var blobs1   = new FakeBlobService();
            var blobs2   = new FakeBlobService();
            var builder1 = new TreeBuilder(blobs1, s_enc, acct1, cont1);
            var builder2 = new TreeBuilder(blobs2, s_enc, acct2, cont2); // different repo-id, but same enc
            var root1    = await builder1.BuildAsync(manifestPath1);
            var root2    = await builder2.BuildAsync(manifestPath2);

            // Same structure, same enc (plaintext), must produce same root hash
            root1.ShouldBe(root2);
        }
        finally
        {
            File.Delete(manifestPath1);
            File.Delete(manifestPath2);
            if (Directory.Exists(cache1)) Directory.Delete(cache1, recursive: true);
            if (Directory.Exists(cache2)) Directory.Delete(cache2, recursive: true);
        }
    }

    [Test]
    public async Task BuildAsync_MetadataChange_DifferentRootHash()
    {
        const string acct = "acc-meta", cont = "con-meta";
        var cacheDir = TreeBuilder.GetDiskCacheDirectory(acct, cont);
        if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);

        var manifestPath1 = Path.GetTempFileName();
        var manifestPath2 = Path.GetTempFileName();
        try
        {
            var now1  = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var now2  = new DateTimeOffset(2025, 1, 1,  0,  0, 0, TimeSpan.Zero);  // different modified
            await File.WriteAllTextAsync(manifestPath1,
                new ManifestEntry("file.txt", "hash1", now1, now1).Serialize() + "\n");
            await File.WriteAllTextAsync(manifestPath2,
                new ManifestEntry("file.txt", "hash1", now1, now2).Serialize() + "\n");

            var blobs1 = new FakeBlobService();
            var blobs2 = new FakeBlobService();
            var root1  = await new TreeBuilder(blobs1, s_enc, acct, cont).BuildAsync(manifestPath1);
            // Clean cache between runs to force independent computation
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
            var root2  = await new TreeBuilder(blobs2, s_enc, acct, cont).BuildAsync(manifestPath2);

            root1.ShouldNotBe(root2);
        }
        finally
        {
            File.Delete(manifestPath1);
            File.Delete(manifestPath2);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Test]
    public async Task BuildAsync_DeduplicatesBlob_WhenAlreadyOnDisk()
    {
        // Pre-populate the disk cache so the builder skips the upload
        var manifestPath = Path.GetTempFileName();
        try
        {
            var now   = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var entry = new ManifestEntry("file.txt", "hash1", now, now);
            await File.WriteAllTextAsync(manifestPath, entry.Serialize() + "\n");

            var blobs   = new FakeBlobService();
            var builder = new TreeBuilder(blobs, s_enc, "acc", "con");

            // First run → should upload
            var root = await builder.BuildAsync(manifestPath);
            var uploadCount1 = blobs.Uploaded.Count;
            uploadCount1.ShouldBeGreaterThan(0);

            // Second run (same builder instance, disk cache still populated)
            var blobs2   = new FakeBlobService();
            // Same disk cache dir (same account/container)
            var builder2 = new TreeBuilder(blobs2, s_enc, "acc", "con");
            var root2    = await builder2.BuildAsync(manifestPath);

            root2.ShouldBe(root);
            // Second run: disk cache hit → no uploads
            blobs2.Uploaded.Count.ShouldBe(0);
        }
        finally
        {
            File.Delete(manifestPath);
            // Clean up disk cache
            var cacheDir = TreeBuilder.GetDiskCacheDirectory("acc", "con");
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
    }
}
