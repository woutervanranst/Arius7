using Arius.Core.Encryption;
using Arius.Core.FileTree;
using Shouldly;

namespace Arius.Core.Tests.FileTree;

// ── 5.1 / 5.2  Model and serialization roundtrip ─────────────────────────────

public class TreeBlobSerializerTests
{
    private static TreeBlob MakeBlob(params (string name, TreeEntryType type, string hash)[] items) =>
        new()
        {
            Entries = items.Select(i => new TreeEntry
            {
                Name     = i.name,
                Type     = i.type,
                Hash     = i.hash,
                Created  = i.type == TreeEntryType.File ? new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero) : null,
                Modified = i.type == TreeEntryType.File ? new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero) : null
            }).ToList()
        };

    [Test]
    public void Serialize_ThenDeserialize_RoundTrips()
    {
        var blob = MakeBlob(
            ("photo.jpg", TreeEntryType.File, "a1b2c3d4"),
            ("subdir/",   TreeEntryType.Dir,  "e5f6a7b8"));

        var json  = TreeBlobSerializer.Serialize(blob);
        var back  = TreeBlobSerializer.Deserialize(json);

        back.Entries.Count.ShouldBe(2);
        back.Entries[0].Name.ShouldBe("photo.jpg");
        back.Entries[0].Type.ShouldBe(TreeEntryType.File);
        back.Entries[0].Hash.ShouldBe("a1b2c3d4");
        back.Entries[0].Created.ShouldNotBeNull();
        back.Entries[1].Name.ShouldBe("subdir/");
        back.Entries[1].Type.ShouldBe(TreeEntryType.Dir);
        back.Entries[1].Created.ShouldBeNull();
    }

    [Test]
    public void Serialize_SortsEntriesByName()
    {
        // Insert entries in reverse order
        var blob = MakeBlob(
            ("z_last.txt",  TreeEntryType.File, "hash3"),
            ("a_first.txt", TreeEntryType.File, "hash1"),
            ("m_mid.txt",   TreeEntryType.File, "hash2"));

        var json = TreeBlobSerializer.Serialize(blob);
        var back = TreeBlobSerializer.Deserialize(json);

        back.Entries[0].Name.ShouldBe("a_first.txt");
        back.Entries[1].Name.ShouldBe("m_mid.txt");
        back.Entries[2].Name.ShouldBe("z_last.txt");
    }

    [Test]
    public void Serialize_IsDeterministic_SameInputSameOutput()
    {
        var blob1 = MakeBlob(("b.jpg", TreeEntryType.File, "hash2"), ("a.jpg", TreeEntryType.File, "hash1"));
        var blob2 = MakeBlob(("a.jpg", TreeEntryType.File, "hash1"), ("b.jpg", TreeEntryType.File, "hash2"));

        var json1 = TreeBlobSerializer.Serialize(blob1);
        var json2 = TreeBlobSerializer.Serialize(blob2);

        json1.ShouldBe(json2);
    }

    [Test]
    public void Serialize_NullTimestamps_OmittedFromJson()
    {
        var blob = new TreeBlob
        {
            Entries =
            [
                new TreeEntry { Name = "sub/", Type = TreeEntryType.Dir, Hash = "abc", Created = null, Modified = null }
            ]
        };
        var json   = TreeBlobSerializer.Serialize(blob);
        var jsonStr = System.Text.Encoding.UTF8.GetString(json);

        jsonStr.ShouldNotContain("created");
        jsonStr.ShouldNotContain("modified");
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

    // Minimal stub IBlobStorageService that records uploads
    private sealed class FakeBlobService : Arius.Core.Storage.IBlobStorageService
    {
        public readonly HashSet<string> Uploaded = new();
        public readonly HashSet<string> HeadChecked = new();

        public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata,
            Storage.BlobTier tier, bool overwrite = false, CancellationToken ct = default)
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

        public Task CopyAsync(string src, string dst, Storage.BlobTier tier, Storage.RehydratePriority? priority = null, CancellationToken ct = default) =>
            Task.CompletedTask;
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
