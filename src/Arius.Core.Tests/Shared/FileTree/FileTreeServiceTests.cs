using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;
using Arius.Core.Tests.Shared.FileTree.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.FileTree;

/// <summary>
/// Unit tests for <see cref="FileTreeService"/> covering tasks 7.1–7.12.
/// Each test uses isolated account/container names to avoid cross-test disk pollution,
/// and cleans up the cache directories in a finally block.
/// </summary>
public class FileTreeServiceTests
{
    private static readonly PlaintextPassthroughService s_enc = new();

    private static readonly DateTimeOffset s_ts1 = new(2024, 1, 1,  0,  0,  0,  TimeSpan.Zero);
    private static readonly DateTimeOffset s_ts2 = new(2024, 6, 15, 10, 0,  0,  TimeSpan.Zero);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<FileTreeEntry> MakeEntries(string fileName = "a.txt", string hash = "aabbccdd") =>
    [
        new FileEntry
        {
            Name        = PathSegment.Parse(fileName),
            ContentHash = ContentHash.Parse(NormalizeHash(hash)),
            Created  = s_ts1,
            Modified = s_ts2
        }
    ];

    private static string NormalizeHash(string hash)
        => hash.Length == 64 ? hash : hash[0].ToString().PadRight(64, char.ToLowerInvariant(hash[0]));

    private static string ResolveCachePath(LocalDirectory cacheDir, FileTreeHash hash)
        => cacheDir.Resolve(FileTreePaths.GetCachePath(hash));

    private static (FileTreeService svc, FakeInMemoryBlobContainerService blobs, LocalDirectory cacheDir, LocalDirectory snapshotsDir) MakeService(string acct, string cont)
    {
        var blobs        = new FakeInMemoryBlobContainerService();
        var index        = new ChunkIndexService(blobs, s_enc, acct, cont);
        var svc          = new FileTreeService(blobs, s_enc, index, acct, cont);
        var cacheDir     = RepositoryPaths.GetFileTreeCacheRoot(acct, cont);
        var snapshotsDir = RepositoryPaths.GetSnapshotCacheRoot(acct, cont);
        new RelativeFileSystem(snapshotsDir).CreateDirectory(RelativePath.Root); // ensure dir exists for tests that seed files directly
        return (svc, blobs, cacheDir, snapshotsDir);
    }

    private static async Task CleanupAsync(LocalDirectory cacheDir, LocalDirectory snapshotsDir)
    {
        new RelativeFileSystem(cacheDir).DeleteDirectory(RelativePath.Root, recursive: true);
        new RelativeFileSystem(snapshotsDir).DeleteDirectory(RelativePath.Root, recursive: true);
        await Task.CompletedTask;
    }

    // ── 7.1  ReadAsync — cache hit ────────────────────────────────────────────

    [Test]
    public async Task ReadAsync_CacheHit_NoAzureCall()
    {
        const string acct = "tc-read-hit", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
        try
        {
            var entries   = MakeEntries();
            var hash      = FileTreeBuilder.ComputeHash(entries, s_enc);
            var diskPath = ResolveCachePath(cacheDir, hash);

            // Pre-populate disk cache with plaintext
            var plaintext = FileTreeSerializer.Serialize(entries);
            await File.WriteAllBytesAsync(diskPath, plaintext);

            var result = await svc.ReadAsync(hash);

            result.Count.ShouldBe(1);
            result[0].Name.ShouldBe(PathSegment.Parse("a.txt"));
            // No Azure download should have been requested
            blobs.RequestedBlobNames.ShouldBeEmpty();
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    // ── 7.2  ReadAsync — cache miss ───────────────────────────────────────────

    [Test]
    public async Task ReadAsync_CacheMiss_DownloadsFromAzureAndWritesToDisk()
    {
        const string acct = "tc-read-miss", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
        try
        {
            var entries   = MakeEntries("photo.jpg", "deadbeef");
            var hash      = FileTreeBuilder.ComputeHash(entries, s_enc);
            var blobName = BlobPaths.FileTreePath(hash);

            // Pre-populate Azure (storage serialization)
            var storageBytes = await SerializeStorageBytesAsync(entries, s_enc);
            blobs.SeedBlob(blobName, storageBytes, contentType: ContentTypes.FileTreePlaintext);
            blobs.RequestedBlobNames.Clear(); // clear seed bookkeeping

            var result = await svc.ReadAsync(hash);

            result.Count.ShouldBe(1);
            result[0].Name.ShouldBe(PathSegment.Parse("photo.jpg"));

            // Azure was called
            blobs.RequestedBlobNames.ShouldContain(blobName);

            // Disk file was written (write-through)
            var diskPath = ResolveCachePath(cacheDir, hash);
            File.Exists(diskPath).ShouldBeTrue();
            (await File.ReadAllBytesAsync(diskPath)).Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    // ── 7.2b ReadAsync — concurrent reads for same hash ──────────────────────

    [Test]
    public async Task ReadAsync_ConcurrentReads_NoDiskCorruption_AtMostOneAzureDownload()
    {
        const string acct = "tc-read-conc", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
        try
        {
            var entries   = MakeEntries("concurrent.txt", "cc001122");
            var hash      = FileTreeBuilder.ComputeHash(entries, s_enc);
            var blobName = BlobPaths.FileTreePath(hash);

            // Pre-populate Azure only — no local cache
            var storageBytes = await SerializeStorageBytesAsync(entries, s_enc);
            blobs.SeedBlob(blobName, storageBytes, contentType: ContentTypes.FileTreePlaintext);
            blobs.RequestedBlobNames.Clear();

            // Fire two concurrent ReadAsync calls for the same hash
            var t1 = svc.ReadAsync(hash);
            var t2 = svc.ReadAsync(hash);
            var results = await Task.WhenAll(t1, t2);

            // Both should return the correct blob
            results[0].Count.ShouldBe(1);
            results[0][0].Name.ShouldBe(PathSegment.Parse("concurrent.txt"));
            results[1].Count.ShouldBe(1);
            results[1][0].Name.ShouldBe(PathSegment.Parse("concurrent.txt"));

            // Disk file must exist and have content (not empty / corrupt)
            var diskPath = ResolveCachePath(cacheDir, hash);
            File.Exists(diskPath).ShouldBeTrue();
            (await File.ReadAllBytesAsync(diskPath)).Length.ShouldBeGreaterThan(0);

            // Azure should have been called at most once (concurrent reads should coalesce
            // around the first download; a second download would indicate missing deduplication)
            blobs.RequestedBlobNames.Count(n => n == blobName).ShouldBeLessThanOrEqualTo(1, "At most one Azure download expected");
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    [Test]
    public async Task ReadAsync_ConcurrentReads_DoesNotExposePartialCacheFile()
    {
        const string acct = "tc-read-conc-partial", cont = "container";
        var blobs = new SlowDownloadBlobContainerService();
        var index = new ChunkIndexService(blobs, s_enc, acct, cont);
        var svc = new FileTreeService(blobs, s_enc, index, acct, cont);
        var cacheDir = RepositoryPaths.GetFileTreeCacheRoot(acct, cont);
        var snapshotsDir = RepositoryPaths.GetSnapshotCacheRoot(acct, cont);
        new RelativeFileSystem(snapshotsDir).CreateDirectory(RelativePath.Root);

        try
        {
            var entries = MakeEntries("partial.txt", "a1b2c3d4");
            var hash = FileTreeBuilder.ComputeHash(entries, s_enc);
            var blobName = BlobPaths.FileTreePath(hash);
            var storageBytes = await SerializeStorageBytesAsync(entries, s_enc);
            blobs.SeedBlob(blobName, storageBytes, contentType: ContentTypes.FileTreePlaintext);

            var t1 = svc.ReadAsync(hash);
            await blobs.FirstDownloadStarted;

            var t2 = svc.ReadAsync(hash);
            blobs.ReleaseFirstDownload();

            var results = await Task.WhenAll(t1, t2);

            results.SelectMany(result => result).All(entry => entry.Name == PathSegment.Parse("partial.txt")).ShouldBeTrue();

            var diskPath = ResolveCachePath(cacheDir, hash);
            new RelativeFileSystem(cacheDir).FileExists(FileTreePaths.GetCachePath(hash)).ShouldBeTrue();
            FileTreeSerializer.Deserialize(await File.ReadAllBytesAsync(diskPath))[0].Name.ShouldBe(PathSegment.Parse("partial.txt"));
            blobs.RequestedBlobNames.Count(n => n == blobName).ShouldBe(1);
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    [Test]
    public async Task ReadAsync_DownloadFailure_PropagatesExceptionWithoutHanging()
    {
        const string acct = "tc-read-failure", cont = "container";
        var expected = new InvalidOperationException("download failed");
        var blobs = new ThrowingDownloadBlobContainerService(expected);
        var index = new ChunkIndexService(blobs, s_enc, acct, cont);
        var svc = new FileTreeService(blobs, s_enc, index, acct, cont);
        var cacheDir = RepositoryPaths.GetFileTreeCacheRoot(acct, cont);
        var snapshotsDir = RepositoryPaths.GetSnapshotCacheRoot(acct, cont);
        new RelativeFileSystem(snapshotsDir).CreateDirectory(RelativePath.Root);

        try
        {
            var hash = FakeFileTreeHash('f');

            var ex = await Should.ThrowAsync<InvalidOperationException>(() => svc.ReadAsync(hash));

            ex.Message.ShouldBe(expected.Message);
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    // ── 7.3  WriteAsync — Azure upload + disk cache write ────────────────────

    [Test]
    public async Task WriteAsync_UploadsToAzureAndWritesToDisk()
    {
        const string acct = "tc-write", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
        try
        {
            var entries = MakeEntries("doc.pdf", "cafebabe");
            var plaintext = FileTreeSerializer.Serialize(entries);
            var payload = (Hash: FileTreeHash.Parse(s_enc.ComputeHash(plaintext)), Plaintext: (ReadOnlyMemory<byte>)plaintext);

            await svc.WriteAsync(payload);

            // Azure was uploaded
            var blobName = BlobPaths.FileTreePath(payload.Hash);
            blobs.UploadedBlobNames.ShouldContain(blobName);

            // Disk file was written
            var diskPath = ResolveCachePath(cacheDir, payload.Hash);
            File.Exists(diskPath).ShouldBeTrue();
            (await File.ReadAllBytesAsync(diskPath)).Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    [Test]
    public async Task WriteAsync_CanonicalPayload_ReusesProvidedPlaintextForUploadAndDiskCache()
    {
        const string acct = "tc-write-canonical", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
        try
        {
            var entries = new List<FileTreeEntry>
            {
                new FileEntry
                {
                    Name = PathSegment.Parse("alpha.txt"),
                    ContentHash = ContentHash.Parse(new string('a', 64)),
                    Created = s_ts1,
                    Modified = s_ts2
                }
            };

            var plaintext = FileTreeSerializer.Serialize(entries);
            var payload = (Hash: FileTreeHash.Parse(s_enc.ComputeHash(plaintext)), Plaintext: (ReadOnlyMemory<byte>)plaintext);
            var expectedPlaintext = payload.Plaintext.ToArray();

            entries[0] = ((FileEntry)entries[0]) with { Name = PathSegment.Parse("omega.txt") };

            await svc.WriteAsync(payload);

            var diskPath = ResolveCachePath(cacheDir, payload.Hash);
            (await File.ReadAllBytesAsync(diskPath)).ShouldBe(expectedPlaintext);

            var blobBytes = await ReadBlobBytesAsync(blobs, BlobPaths.FileTreePath(payload.Hash));
            await using var gzipStream = new System.IO.Compression.GZipStream(new MemoryStream(blobBytes), System.IO.Compression.CompressionMode.Decompress);
            using var plaintextStream = new MemoryStream();
            await gzipStream.CopyToAsync(plaintextStream);
            plaintextStream.ToArray().ShouldBe(expectedPlaintext);
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    [Test]
    public async Task WriteAsync_WithPassphrase_UploadsEncryptedFileTreeBlob()
    {
        const string acct = "tc-write-passphrase", cont = "container";
        var encryption = new PassphraseEncryptionService("test-passphrase");
        var blobs = new FakeInMemoryBlobContainerService();
        var chunkIndex = new ChunkIndexService(blobs, encryption, acct, cont);
        var service = new FileTreeService(blobs, encryption, chunkIndex, acct, cont);
        var cacheDir = RepositoryPaths.GetFileTreeCacheRoot(acct, cont);
        var snapshotsDir = RepositoryPaths.GetSnapshotCacheRoot(acct, cont);
        new RelativeFileSystem(snapshotsDir).CreateDirectory(RelativePath.Root);

        try
        {
            IReadOnlyList<FileTreeEntry> entries =
            [
                new FileEntry
                {
                    Name = PathSegment.Parse("photo.jpg"),
                    ContentHash = ContentHash.Parse(new string('a', 64)),
                    Created = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero),
                    Modified = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero)
                }
            ];

            var plaintext = FileTreeSerializer.Serialize(entries);
            var payload = (Hash: FileTreeHash.Parse(encryption.ComputeHash(plaintext)), Plaintext: (ReadOnlyMemory<byte>)plaintext);

            await service.WriteAsync(payload);

            var blobName = BlobPaths.FileTreePath(payload.Hash);
            var uploadedBytes = await ReadBlobBytesAsync(blobs, blobName);
            var prefix = System.Text.Encoding.ASCII.GetString(uploadedBytes[..6]);
            prefix.ShouldBe("ArGCM1");

            var text = System.Text.Encoding.UTF8.GetString(uploadedBytes);
            text.ShouldNotContain("photo.jpg");
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    [Test]
    public async Task ReadAsync_WithPassphrase_DownloadedBlobRoundTripsToEntries()
    {
        const string acct = "tc-read-passphrase", cont = "container";
        var encryption = new PassphraseEncryptionService("test-passphrase");
        var blobs = new FakeInMemoryBlobContainerService();
        var chunkIndex = new ChunkIndexService(blobs, encryption, acct, cont);
        var service = new FileTreeService(blobs, encryption, chunkIndex, acct, cont);
        var cacheDir = RepositoryPaths.GetFileTreeCacheRoot(acct, cont);
        var snapshotsDir = RepositoryPaths.GetSnapshotCacheRoot(acct, cont);
        new RelativeFileSystem(snapshotsDir).CreateDirectory(RelativePath.Root);

        try
        {
            IReadOnlyList<FileTreeEntry> entries =
            [
                new FileEntry
                {
                    Name = PathSegment.Parse("photo.jpg"),
                    ContentHash = ContentHash.Parse(new string('a', 64)),
                    Created = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero),
                    Modified = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero)
                },
                new DirectoryEntry
                {
                    Name = PathSegment.Parse("subdir"),
                    FileTreeHash = FileTreeHash.Parse(new string('e', 64))
                }
            ];

            var plaintext = FileTreeSerializer.Serialize(entries);
            var payload = (Hash: FileTreeHash.Parse(encryption.ComputeHash(plaintext)), Plaintext: (ReadOnlyMemory<byte>)plaintext);

            await service.WriteAsync(payload);
            var roundTripped = await service.ReadAsync(payload.Hash);

            roundTripped.ShouldBe(entries);
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    // ── 7.4  WriteAsync — BlobAlreadyExistsException is swallowed ────────────

    [Test]
    public async Task WriteAsync_BlobAlreadyExists_DiskCacheStillWritten()
    {
        const string acct = "tc-write-dup", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
        try
        {
            var entries   = MakeEntries();
            var plaintext = FileTreeSerializer.Serialize(entries);
            var payload   = (Hash: FileTreeHash.Parse(s_enc.ComputeHash(plaintext)), Plaintext: (ReadOnlyMemory<byte>)plaintext);

            // Seed blob in Azure so upload throws BlobAlreadyExistsException
            var storageBytes = await SerializeStorageBytesAsync(entries, s_enc);
            blobs.SeedBlob(BlobPaths.FileTreePath(payload.Hash), storageBytes, contentType: ContentTypes.FileTreePlaintext);

            // Should not throw
            await Should.NotThrowAsync(() => svc.WriteAsync(payload));

            // Disk file should still be written
            var diskPath = ResolveCachePath(cacheDir, payload.Hash);
            File.Exists(diskPath).ShouldBeTrue();
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    [Test]
    public async Task EnsureStoredAsync_PayloadOverload_WritesMissingTree()
    {
        var blobs = new FakeRecordingBlobContainerService();
        var account = $"acc-{Guid.NewGuid():N}";
        var container = $"con-{Guid.NewGuid():N}";
        var encryption = new PlaintextPassthroughService();
        var chunkIndex = new ChunkIndexService(blobs, encryption, account, container);
        var service = new FileTreeService(blobs, encryption, chunkIndex, account, container);
        var cacheDir = RepositoryPaths.GetFileTreeCacheRoot(account, container);
        var snapshotsDir = RepositoryPaths.GetSnapshotCacheRoot(account, container);
        try
        {
            await service.ValidateAsync();
            IReadOnlyList<FileTreeEntry> entries =
            [
                new FileEntry
                {
                    Name = PathSegment.Parse("readme.txt"),
                    ContentHash = ContentHash.Parse(new string('c', 64)),
                    Created = DateTimeOffset.UnixEpoch,
                    Modified = DateTimeOffset.UnixEpoch
                }
            ];

            var plaintext = FileTreeSerializer.Serialize(entries);
            var payload = (Hash: FileTreeHash.Parse(encryption.ComputeHash(plaintext)), Plaintext: (ReadOnlyMemory<byte>)plaintext);
            await service.EnsureStoredAsync(payload);

            blobs.Uploaded.ShouldContain(BlobPaths.FileTreePath(payload.Hash));
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    private static async Task<byte[]> ReadBlobBytesAsync(FakeInMemoryBlobContainerService blobs, RelativePath blobName)
    {
        await using var stream = await blobs.DownloadAsync(blobName);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static async Task<byte[]> SerializeStorageBytesAsync(IReadOnlyList<FileTreeEntry> entries, IEncryptionService encryption)
    {
        var plaintext = FileTreeSerializer.Serialize(entries);
        var ms = new MemoryStream();

        await using (var encStream = encryption.WrapForEncryption(ms))
        await using (var gzipStream = new System.IO.Compression.GZipStream(encStream, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await gzipStream.WriteAsync(plaintext);
        }

        return ms.ToArray();
    }

    // ── 7.5  ValidateAsync — snapshot match — no filetrees listing ────────────

    [Test]
    public async Task ValidateAsync_SnapshotMatch_NoFiletreesListing()
    {
        const string acct = "tc-val-match", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
        var snapshotsFileSystem = new RelativeFileSystem(snapshotsDir);
        try
        {
            var timestamp = "2024-06-15T100000.000Z";

            // Write a local snapshot marker
            await snapshotsFileSystem.WriteAllBytesAsync(RelativePath.Parse(timestamp), [], CancellationToken.None);

            // Seed a remote snapshot with the same timestamp
            blobs.SeedBlob(BlobPaths.SnapshotPath(timestamp), [], contentType: null);

            await svc.ValidateAsync();

            // No filetrees listing should have been performed
            // (ListAsync is only called for "filetrees/", not "snapshots/")
            // We verify by checking no filetree blobs were requested
            blobs.RequestedBlobNames.ShouldBeEmpty();
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    [Test]
    public async Task ValidateAsync_RemoteSnapshotsOutOfOrder_SortsBeforeChoosingLatest()
    {
        const string acct = "tc-val-unsorted", cont = "container";
        var blobs = new UnsortedSnapshotBlobContainerService(
            [
                BlobPaths.SnapshotPath("2024-06-15T100000.000Z"),
                BlobPaths.SnapshotPath("2024-01-01T000000.000Z")
            ]);
        var index = new ChunkIndexService(blobs, s_enc, acct, cont);
        var svc = new FileTreeService(blobs, s_enc, index, acct, cont);
        var cacheDir = RepositoryPaths.GetFileTreeCacheRoot(acct, cont);
        var snapshotsDir = RepositoryPaths.GetSnapshotCacheRoot(acct, cont);
        var snapshotsFileSystem = new RelativeFileSystem(snapshotsDir);
        snapshotsFileSystem.CreateDirectory(RelativePath.Root);

        try
        {
            await snapshotsFileSystem.WriteAllBytesAsync(RelativePath.Parse("2024-06-15T100000.000Z"), [], CancellationToken.None);

            await svc.ValidateAsync();

            blobs.FileTreesListed.ShouldBeFalse();
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    [Test]
    public async Task ValidateAsync_IgnoresSiblingSnapshotLikePrefixes()
    {
        const string acct = "tc-val-sibling-prefix", cont = "container";
        var blobs = new UnsortedSnapshotBlobContainerService(
            [
                BlobPaths.SnapshotPath("2024-06-15T100000.000Z"),
                RelativePath.Parse("snapshots-old/2024-07-01T100000.000Z")
            ]);
        var index = new ChunkIndexService(blobs, s_enc, acct, cont);
        var svc = new FileTreeService(blobs, s_enc, index, acct, cont);
        var cacheDir = RepositoryPaths.GetFileTreeCacheRoot(acct, cont);
        var snapshotsDir = RepositoryPaths.GetSnapshotCacheRoot(acct, cont);
        var snapshotsFileSystem = new RelativeFileSystem(snapshotsDir);
        snapshotsFileSystem.CreateDirectory(RelativePath.Root);

        try
        {
            await snapshotsFileSystem.WriteAllBytesAsync(RelativePath.Parse("2024-06-15T100000.000Z"), [], CancellationToken.None);

            await svc.ValidateAsync();

            blobs.FileTreesListed.ShouldBeFalse();
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    // ── 7.6  ValidateAsync — mismatch — creates markers + deletes L2 ──────────

    [Test]
    public async Task ValidateAsync_SnapshotMismatch_MarkerFilesCreated_L2Deleted()
    {
        const string acct = "tc-val-miss", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
        var snapshotsFileSystem = new RelativeFileSystem(snapshotsDir);

        // Determine the L2 dir and pre-populate it with a dummy file
        var chunkL2Dir = RepositoryPaths.GetChunkIndexCacheRoot(acct, cont);
        var chunkL2FileSystem = new RelativeFileSystem(chunkL2Dir);
        chunkL2FileSystem.CreateDirectory(RelativePath.Root);
        var dummyL2File = RelativePath.Parse("dummy-shard.dat");
        await chunkL2FileSystem.WriteAllTextAsync(dummyL2File, "stale", CancellationToken.None);

        try
        {
            var firstRemoteHash = FakeFileTreeHash('1');
            var secondRemoteHash = FakeFileTreeHash('2');

            // Local marker: old snapshot
            await snapshotsFileSystem.WriteAllBytesAsync(RelativePath.Parse("2024-01-01T000000.000Z"), [], CancellationToken.None);

            // Remote snapshot: newer
            blobs.SeedBlob(BlobPaths.SnapshotPath("2024-06-15T100000.000Z"), [], contentType: null);

            // Two remote filetree blobs
            blobs.SeedBlob(BlobPaths.FileTreePath(firstRemoteHash), [], contentType: null);
            blobs.SeedBlob(BlobPaths.FileTreePath(secondRemoteHash), [], contentType: null);
            await svc.ValidateAsync();

            // Empty marker files created for remote filetrees
            File.Exists(ResolveCachePath(cacheDir, firstRemoteHash)).ShouldBeTrue();
            File.Exists(ResolveCachePath(cacheDir, secondRemoteHash)).ShouldBeTrue();

            // L2 dummy file deleted
            chunkL2FileSystem.FileExists(dummyL2File).ShouldBeFalse();
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
            chunkL2FileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }
    }

    // ── 7.7  ValidateAsync — mismatch does NOT overwrite existing cache files ──

    [Test]
    public async Task ValidateAsync_Mismatch_DoesNotOverwriteExistingCacheFiles()
    {
        const string acct = "tc-val-noover", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
        var snapshotsFileSystem = new RelativeFileSystem(snapshotsDir);
        var cacheFileSystem = new RelativeFileSystem(cacheDir);
        try
        {
            var existingHash = FakeFileTreeHash('3');

            // Local marker: old snapshot
            await snapshotsFileSystem.WriteAllBytesAsync(RelativePath.Parse("2024-01-01T000000.000Z"), [], CancellationToken.None);

            // Remote snapshot: newer
            blobs.SeedBlob(BlobPaths.SnapshotPath("2024-06-15T100000.000Z"), [], contentType: null);

            // One remote blob already present in cache with real content
            var existingContent = new byte[] { 1, 2, 3, 4, 5 };
            var cachePath = RelativePath.Parse(existingHash.ToString());
            await cacheFileSystem.WriteAllBytesAsync(cachePath, existingContent, CancellationToken.None);

            blobs.SeedBlob(BlobPaths.FileTreePath(existingHash), [], contentType: null);

            await svc.ValidateAsync();

            // Existing file content must be preserved (not overwritten with empty marker)
            var after = await cacheFileSystem.ReadAllBytesAsync(cachePath, CancellationToken.None);
            after.ShouldBe(existingContent);
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    // ── 7.8  ValidateAsync — no local markers — slow path triggered ───────────

    [Test]
    public async Task ValidateAsync_NoLocalMarkers_SlowPathTriggered()
    {
        const string acct = "tc-val-noloc", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
        var cacheFileSystem = new RelativeFileSystem(cacheDir);
        try
        {
            var remoteHash = FakeFileTreeHash('4');

            // No local snapshot markers (snapshotsDir is empty)

            // Remote snapshot exists
            blobs.SeedBlob(BlobPaths.SnapshotPath("2024-06-15T100000.000Z"), [], contentType: null);

            // Remote filetree blob
            blobs.SeedBlob(BlobPaths.FileTreePath(remoteHash), [], contentType: null);

            await svc.ValidateAsync();

            // Slow path should create marker for remote blob
            cacheFileSystem.FileExists(RelativePath.Parse(remoteHash.ToString())).ShouldBeTrue();
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    // ── 7.9  ValidateAsync — no remote snapshots — fast path (empty repo) ─────

    [Test]
    public async Task ValidateAsync_NoRemoteSnapshots_FastPath()
    {
        const string acct = "tc-val-empty", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
        try
        {
            // No remote blobs at all — ListAsync returns empty for "snapshots/"
            await svc.ValidateAsync();

            // Should not throw and should be marked validated (ExistsInRemote works)
            Should.NotThrow(() => svc.ExistsInRemote(FakeFileTreeHash('a')));
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    // ── 7.10  ExistsInRemote — File.Exists check ─────────────────────────────

    [Test]
    public async Task ExistsInRemote_ReturnsTrue_WhenDiskFileExists()
    {
        const string acct = "tc-exists-true", cont = "container";
        var (svc, _, cacheDir, snapshotsDir) = MakeService(acct, cont);
        var cacheFileSystem = new RelativeFileSystem(cacheDir);
        try
        {
            await svc.ValidateAsync();
            var hash = FakeFileTreeHash('a');
            await cacheFileSystem.WriteAllBytesAsync(RelativePath.Parse(hash.ToString()), [], CancellationToken.None);

            svc.ExistsInRemote(hash).ShouldBeTrue();
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    [Test]
    public async Task ExistsInRemote_ReturnsFalse_WhenDiskFileAbsent()
    {
        const string acct = "tc-exists-false", cont = "container";
        var (svc, _, cacheDir, snapshotsDir) = MakeService(acct, cont);
        try
        {
            await svc.ValidateAsync();

            svc.ExistsInRemote(FakeFileTreeHash('b')).ShouldBeFalse();
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    // ── 7.11  ExistsInRemote — throws before ValidateAsync ───────────────────

    [Test]
    public void ExistsInRemote_BeforeValidateAsync_ThrowsInvalidOperationException()
    {
        const string acct = "tc-exists-guard", cont = "container";
        var (svc, _, cacheDir, snapshotsDir) = MakeService(acct, cont);
        try
        {
            Should.Throw<InvalidOperationException>(() => svc.ExistsInRemote(FakeFileTreeHash('a')));
        }
        finally
        {
            new RelativeFileSystem(cacheDir).DeleteDirectory(RelativePath.Root, recursive: true);
            new RelativeFileSystem(snapshotsDir).DeleteDirectory(RelativePath.Root, recursive: true);
        }
    }

    // ── 7.12  SnapshotService.CreateAsync — write-through to disk ────────────

    [Test]
    public async Task SnapshotService_CreateAsync_WritesPlainJsonToDisk()
    {
        const string acct = "tc-marker", cont = "container";
        var blobs        = new FakeInMemoryBlobContainerService();
        var snapshotSvc  = new SnapshotService(blobs, s_enc, acct, cont);
        var snapshotsDir = RepositoryPaths.GetSnapshotCacheRoot(acct, cont);
        var snapshotsFileSystem = new RelativeFileSystem(snapshotsDir);
        try
        {
            var ts       = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var rootHash = FileTreeHash.Parse("aabbccdd" + new string('0', 56));
            var manifest = await snapshotSvc.CreateAsync(rootHash, fileCount: 5, totalSize: 512, timestamp: ts);

            var expectedFileName = ts.UtcDateTime.ToString(SnapshotService.TimestampFormat);
            var localPath        = RelativePath.Parse(expectedFileName);

            // Disk file exists and is valid JSON
            snapshotsFileSystem.FileExists(localPath).ShouldBeTrue();
            var json = await snapshotsFileSystem.ReadAllTextAsync(localPath, CancellationToken.None);
            json.ShouldContain(rootHash.ToString());

            // Azure was also uploaded
            blobs.UploadedBlobNames.ShouldContain(SnapshotService.BlobName(ts));
        }
        finally
        {
            snapshotsFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }
    }

    // ── 7.x  ValidateAsync — idempotent (called twice, no double slow-path) ───

    [Test]
    public async Task ValidateAsync_IsIdempotent_SecondCallIsNoOp()
    {
        const string acct = "tc-val-idempotent", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
        try
        {
            var someHash = FakeFileTreeHash('5');

            // Remote snapshot mismatch forces slow path on first call
            blobs.SeedBlob(BlobPaths.SnapshotPath("2024-06-15T100000.000Z"), [], contentType: null);
            blobs.SeedBlob(BlobPaths.FileTreePath(someHash), [], contentType: null);

            await svc.ValidateAsync();

            // Clear tracking data and call again — no new listing should happen
            blobs.UploadedBlobNames.Clear();
            blobs.RequestedBlobNames.Clear();

            await svc.ValidateAsync(); // should be a no-op

            // No Azure calls on second invocation
            blobs.RequestedBlobNames.ShouldBeEmpty();
            blobs.UploadedBlobNames.ShouldBeEmpty();
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

}
