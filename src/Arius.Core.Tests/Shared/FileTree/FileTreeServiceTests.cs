using System.IO.Compression;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Tests.Fakes;
using Arius.Core.Tests.Shared.FileTree.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.FileTree;

/// <summary>
/// Unit tests for <see cref="FileTreeService"/> covering tasks 7.1–7.12.
/// Each test uses isolated account/container names and relies on <see cref="RepositoryTestFixture"/>
/// for repository service wiring and repository cache cleanup.
/// </summary>
public class FileTreeServiceTests
{

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

    // ── 7.1  ReadAsync — cache hit ────────────────────────────────────────────

    [Test]
    public async Task ReadAsync_CacheHit_NoAzureCall()
    {
        const string acct = "tc-read-hit", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);

        var entries   = MakeEntries();
        var hash      = FileTreeBuilder.ComputeHash(entries, IEncryptionService.PlaintextInstance);
        var diskPath = ResolveCachePath(fixture.FileTreeCacheDirectory, hash);

        // Pre-populate disk cache with plaintext
        var plaintext = FileTreeSerializer.Serialize(entries);
        await File.WriteAllBytesAsync(diskPath, plaintext);

        var result = await fixture.FileTreeService.ReadAsync(hash);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe(PathSegment.Parse("a.txt"));
        // No Azure download should have been requested.
        blobs.RequestedBlobNames.ShouldNotContain(BlobPaths.FileTreesPrefix);
    }

    // ── 7.2  ReadAsync — cache miss ───────────────────────────────────────────

    [Test]
    public async Task ReadAsync_CacheMiss_DownloadsFromAzureAndWritesToDisk()
    {
        const string acct = "unittest-tc-read-miss";
        const string cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);

        var entries   = MakeEntries("photo.jpg", "deadbeef");
        var hash      = FileTreeBuilder.ComputeHash(entries, IEncryptionService.PlaintextInstance);
        var blobName = BlobPaths.FileTreePath(hash);

        // Pre-populate Azure (storage serialization).
        var storageBytes = await SerializeStorageBytesAsync(entries, IEncryptionService.PlaintextInstance);
        blobs.SeedBlob(blobName, storageBytes, contentType: ContentTypes.FileTreePlaintext);
        // Clear seed bookkeeping.
        blobs.RequestedBlobNames.Clear();

        var result = await fixture.FileTreeService.ReadAsync(hash);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe(PathSegment.Parse("photo.jpg"));
        // Azure was called.
        blobs.RequestedBlobNames.ShouldContain(blobName);

        // Disk file was written (write-through).
        var diskPath = ResolveCachePath(fixture.FileTreeCacheDirectory, hash);
        File.Exists(diskPath).ShouldBeTrue();
        (await File.ReadAllBytesAsync(diskPath)).Length.ShouldBeGreaterThan(0);
    }

    // ── 7.2b ReadAsync — concurrent reads for same hash ──────────────────────

    [Test]
    public async Task ReadAsync_ConcurrentReads_NoDiskCorruption_AtMostOneAzureDownload()
    {
        const string acct = "unittest-tc-read-conc";
        const string cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);

        var entries   = MakeEntries("concurrent.txt", "cc001122");
        var hash      = FileTreeBuilder.ComputeHash(entries, IEncryptionService.PlaintextInstance);
        var blobName = BlobPaths.FileTreePath(hash);

        // Pre-populate Azure only; no local cache.
        var storageBytes = await SerializeStorageBytesAsync(entries, IEncryptionService.PlaintextInstance);
        blobs.SeedBlob(blobName, storageBytes, contentType: ContentTypes.FileTreePlaintext);
        blobs.RequestedBlobNames.Clear();

        // Fire two concurrent ReadAsync calls for the same hash.
        var t1 = fixture.FileTreeService.ReadAsync(hash);
        var t2 = fixture.FileTreeService.ReadAsync(hash);
        var results = await Task.WhenAll(t1, t2);

        // Both should return the correct blob.
        results[0].Count.ShouldBe(1);
        results[0][0].Name.ShouldBe(PathSegment.Parse("concurrent.txt"));
        results[1].Count.ShouldBe(1);
        results[1][0].Name.ShouldBe(PathSegment.Parse("concurrent.txt"));

        // Disk file must exist and have content.
        var diskPath = ResolveCachePath(fixture.FileTreeCacheDirectory, hash);
        File.Exists(diskPath).ShouldBeTrue();
        (await File.ReadAllBytesAsync(diskPath)).Length.ShouldBeGreaterThan(0);
        // Azure should have been called at most once; concurrent reads should coalesce
        // around the first download, and a second download would indicate missing deduplication.
        blobs.RequestedBlobNames.Count(n => n == blobName).ShouldBeLessThanOrEqualTo(1, "At most one Azure download expected");
    }

    [Test]
    public async Task ReadAsync_ConcurrentReads_DoesNotExposePartialCacheFile()
    {
        const string acct = "unittest-tc-read-conc-partial";
        const string cont = "container";
        var blobs = new SlowDownloadBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);

        var entries = MakeEntries("partial.txt", "a1b2c3d4");
        var hash = FileTreeBuilder.ComputeHash(entries, IEncryptionService.PlaintextInstance);
        var blobName = BlobPaths.FileTreePath(hash);
        var storageBytes = await SerializeStorageBytesAsync(entries, IEncryptionService.PlaintextInstance);
        blobs.SeedBlob(blobName, storageBytes, contentType: ContentTypes.FileTreePlaintext);

        var t1 = fixture.FileTreeService.ReadAsync(hash);
        await blobs.FirstDownloadStarted;

        var t2 = fixture.FileTreeService.ReadAsync(hash);
        blobs.ReleaseFirstDownload();

        var results = await Task.WhenAll(t1, t2);

        results.SelectMany(result => result).All(entry => entry.Name == PathSegment.Parse("partial.txt")).ShouldBeTrue();

        var diskPath = ResolveCachePath(fixture.FileTreeCacheDirectory, hash);
        new RelativeFileSystem(fixture.FileTreeCacheDirectory).FileExists(FileTreePaths.GetCachePath(hash)).ShouldBeTrue();
        FileTreeSerializer.Deserialize(await File.ReadAllBytesAsync(diskPath))[0].Name.ShouldBe(PathSegment.Parse("partial.txt"));
        blobs.RequestedBlobNames.Count(n => n == blobName).ShouldBe(1);
    }

    [Test]
    public async Task ReadAsync_DownloadFailure_PropagatesExceptionWithoutHanging()
    {
        const string acct = "unittest-tc-read-failure";
        const string cont = "container";
        var expected = new InvalidOperationException("download failed");
        var blobs = new ThrowingDownloadBlobContainerService(expected);
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);

        var hash = FakeFileTreeHash('f');

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => fixture.FileTreeService.ReadAsync(hash));

        ex.Message.ShouldBe(expected.Message);
    }

    // ── 7.3  WriteAsync — Azure upload + disk cache write ────────────────────

    [Test]
    public async Task WriteAsync_UploadsToAzureAndWritesToDisk()
    {
        const string acct = "tc-write", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);

        var entries = MakeEntries("doc.pdf", "cafebabe");
        var plaintext = FileTreeSerializer.Serialize(entries);
        var payload = (Hash: FileTreeHashOf(plaintext, IEncryptionService.PlaintextInstance), Plaintext: (ReadOnlyMemory<byte>)plaintext);

        await fixture.FileTreeService.WriteAsync(payload);

        var blobName = BlobPaths.FileTreePath(payload.Hash);
        blobs.UploadedBlobNames.ShouldContain(blobName);

        // Disk file was written.
        var diskPath = ResolveCachePath(fixture.FileTreeCacheDirectory, payload.Hash);
        File.Exists(diskPath).ShouldBeTrue();
        (await File.ReadAllBytesAsync(diskPath)).Length.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task WriteAsync_CanonicalPayload_ReusesProvidedPlaintextForUploadAndDiskCache()
    {
        const string acct = "tc-write-canonical", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);

        List<FileTreeEntry> entries =
        [
            new FileEntry
            {
                Name        = PathSegment.Parse("alpha.txt"),
                ContentHash = ContentHash.Parse(new string('a', 64)),
                Created     = s_ts1,
                Modified    = s_ts2
            }
        ];

        var plaintext = FileTreeSerializer.Serialize(entries);
        var payload = (Hash: FileTreeHashOf(plaintext, IEncryptionService.PlaintextInstance), Plaintext: (ReadOnlyMemory<byte>)plaintext);
        var expectedPlaintext = payload.Plaintext.ToArray();

        entries[0] = ((FileEntry)entries[0]) with { Name = PathSegment.Parse("omega.txt") };

        await fixture.FileTreeService.WriteAsync(payload);

        var diskPath = ResolveCachePath(fixture.FileTreeCacheDirectory, payload.Hash);
        (await File.ReadAllBytesAsync(diskPath)).ShouldBe(expectedPlaintext);

        var blobBytes = await ReadBlobBytesAsync(blobs, BlobPaths.FileTreePath(payload.Hash));
        await using var decompressedStream = ICompressionService.ZtdInstance.WrapForDecompression(new MemoryStream(blobBytes));
        using var plaintextStream = new MemoryStream();
        await decompressedStream.CopyToAsync(plaintextStream);
        plaintextStream.ToArray().ShouldBe(expectedPlaintext);
    }

    [Test]
    public async Task WriteAsync_WithPassphrase_UploadsEncryptedFileTreeBlob()
    {
        const string acct = "tc-write-passphrase", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithPassphraseAsync(blobs, acct, cont);

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
        var payload = (Hash: FileTreeHashOf(plaintext, fixture.Encryption), Plaintext: (ReadOnlyMemory<byte>)plaintext);

        await fixture.FileTreeService.WriteAsync(payload);

        var blobName = BlobPaths.FileTreePath(payload.Hash);
        var uploadedBytes = await ReadBlobBytesAsync(blobs, blobName);
        var prefix = System.Text.Encoding.ASCII.GetString(uploadedBytes[..6]);
        prefix.ShouldBe("ArGCM1");

        var text = System.Text.Encoding.UTF8.GetString(uploadedBytes);
        text.ShouldNotContain("photo.jpg");
    }

    [Test]
    public async Task ReadAsync_WithPassphrase_DownloadedBlobRoundTripsToEntries()
    {
        const string acct = "tc-read-passphrase", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithPassphraseAsync(blobs, acct, cont);

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
        var payload = (Hash: FileTreeHashOf(plaintext, fixture.Encryption), Plaintext: (ReadOnlyMemory<byte>)plaintext);

        await fixture.FileTreeService.WriteAsync(payload);
        var roundTripped = await fixture.FileTreeService.ReadAsync(payload.Hash);

        roundTripped.ShouldBe(entries);
    }

    // ── 7.4  WriteAsync — BlobAlreadyExistsException is swallowed ────────────

    [Test]
    public async Task WriteAsync_BlobAlreadyExists_DiskCacheStillWritten()
    {
        const string acct = "tc-write-dup", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);

        var entries   = MakeEntries();
        var plaintext = FileTreeSerializer.Serialize(entries);
        var payload   = (Hash: FileTreeHashOf(plaintext, IEncryptionService.PlaintextInstance), Plaintext: (ReadOnlyMemory<byte>)plaintext);

        // Seed blob in Azure so upload throws BlobAlreadyExistsException.
        var storageBytes = await SerializeStorageBytesAsync(entries, IEncryptionService.PlaintextInstance);
        blobs.SeedBlob(BlobPaths.FileTreePath(payload.Hash), storageBytes, contentType: ContentTypes.FileTreePlaintext);

        // Should not throw.
        await Should.NotThrowAsync(() => fixture.FileTreeService.WriteAsync(payload));

        // Disk file should still be written.
        var diskPath = ResolveCachePath(fixture.FileTreeCacheDirectory, payload.Hash);
        File.Exists(diskPath).ShouldBeTrue();
    }

    [Test]
    public async Task EnsureStoredAsync_PayloadOverload_WritesMissingTree()
    {
        var blobs = new FakeRecordingBlobContainerService();
        var account = $"acc-{Guid.NewGuid():N}";
        var container = $"con-{Guid.NewGuid():N}";
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, account, container, IEncryptionService.PlaintextInstance);

        await fixture.FileTreeService.ValidateAsync();
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
        var payload = (Hash: FileTreeHashOf(plaintext, IEncryptionService.PlaintextInstance), Plaintext: (ReadOnlyMemory<byte>)plaintext);
        await fixture.FileTreeService.EnsureStoredAsync(payload);

        blobs.Uploaded.Keys.ShouldContain(BlobPaths.FileTreePath(payload.Hash));
    }

    private static async Task<byte[]> ReadBlobBytesAsync(FakeInMemoryBlobContainerService blobs, RelativePath blobName)
    {
        var download = await blobs.DownloadAsync(blobName);
        await using var stream = download.Stream;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static async Task<byte[]> SerializeStorageBytesAsync(IReadOnlyList<FileTreeEntry> entries, IEncryptionService encryption)
    {
        var plaintext = FileTreeSerializer.Serialize(entries);
        var ms = new MemoryStream();

        await using (var encStream = encryption.WrapForEncryption(ms))
        await using (var gzipStream = new GZipStream(encStream, CompressionLevel.SmallestSize, leaveOpen: true))
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
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);
        var snapshotsFileSystem = new RelativeFileSystem(fixture.SnapshotCacheDirectory);

        var timestamp = "2024-06-15T100000.000Z";

        await snapshotsFileSystem.WriteAllBytesAsync(RelativePath.Parse(timestamp), [], CancellationToken.None);
        blobs.SeedBlob(BlobPaths.SnapshotPath(timestamp), [], contentType: null);

        await fixture.FileTreeService.ValidateAsync();

        // No filetrees listing should have been performed.
        // (ListAsync is only called for filetrees/, not snapshots/.)
        // We verify by checking no filetree blobs were requested.
        blobs.RequestedBlobNames.ShouldNotContain(BlobPaths.FileTreesPrefix);
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
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);
        var snapshotsFileSystem = new RelativeFileSystem(fixture.SnapshotCacheDirectory);

        await snapshotsFileSystem.WriteAllBytesAsync(RelativePath.Parse("2024-06-15T100000.000Z"), [], CancellationToken.None);

        await fixture.FileTreeService.ValidateAsync();

        blobs.FileTreesListed.ShouldBeFalse();
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
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);
        var snapshotsFileSystem = new RelativeFileSystem(fixture.SnapshotCacheDirectory);

        await snapshotsFileSystem.WriteAllBytesAsync(RelativePath.Parse("2024-06-15T100000.000Z"), [], CancellationToken.None);

        await fixture.FileTreeService.ValidateAsync();

        blobs.FileTreesListed.ShouldBeFalse();
    }

    // ── 7.6  ValidateAsync — mismatch — creates markers ──────────

    [Test]
    public async Task ValidateAsync_SnapshotMismatch_MarkerFilesCreated_AndReturnsMismatch()
    {
        const string acct = "tc-val-miss", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);
        var snapshotsFileSystem = new RelativeFileSystem(fixture.SnapshotCacheDirectory);

        // Determine the L2 dir and pre-populate it with a dummy file. FileTreeService no longer owns chunk-index invalidation.
        var chunkL2FileSystem = new RelativeFileSystem(fixture.ChunkIndexCacheDirectory);
        chunkL2FileSystem.CreateDirectory(RelativePath.Root);
        var dummyL2File = RelativePath.Parse("dummy-shard.dat");
        await chunkL2FileSystem.WriteAllTextAsync(dummyL2File, "stale", CancellationToken.None);

        var firstRemoteHash = FakeFileTreeHash('1');
        var secondRemoteHash = FakeFileTreeHash('2');

        // Local marker: old snapshot.
        await snapshotsFileSystem.WriteAllBytesAsync(RelativePath.Parse("2024-01-01T000000.000Z"), [], CancellationToken.None);

        // Remote snapshot: newer.
        blobs.SeedBlob(BlobPaths.SnapshotPath("2024-06-15T100000.000Z"), [], contentType: null);

        // Two remote filetree blobs.
        blobs.SeedBlob(BlobPaths.FileTreePath(firstRemoteHash), [], contentType: null);
        blobs.SeedBlob(BlobPaths.FileTreePath(secondRemoteHash), [], contentType: null);
        var result = await fixture.FileTreeService.ValidateAsync();

        result.SnapshotMismatch.ShouldBeTrue();

        // Empty marker files created for remote filetrees.
        File.Exists(ResolveCachePath(fixture.FileTreeCacheDirectory, firstRemoteHash)).ShouldBeTrue();
        File.Exists(ResolveCachePath(fixture.FileTreeCacheDirectory, secondRemoteHash)).ShouldBeTrue();

        // Chunk-index L2 remains untouched; archive coordinates mutable index invalidation.
        chunkL2FileSystem.FileExists(dummyL2File).ShouldBeTrue();
    }

    // ── 7.7  ValidateAsync — mismatch does NOT overwrite existing cache files ──

    [Test]
    public async Task ValidateAsync_Mismatch_DoesNotOverwriteExistingCacheFiles()
    {
        const string acct = "tc-val-noover", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);
        var snapshotsFileSystem = new RelativeFileSystem(fixture.SnapshotCacheDirectory);
        var cacheFileSystem = new RelativeFileSystem(fixture.FileTreeCacheDirectory);
        var existingHash = FakeFileTreeHash('3');

        // Local marker: old snapshot.
        await snapshotsFileSystem.WriteAllBytesAsync(RelativePath.Parse("2024-01-01T000000.000Z"), [], CancellationToken.None);
        // Remote snapshot: newer.
        blobs.SeedBlob(BlobPaths.SnapshotPath("2024-06-15T100000.000Z"), [], contentType: null);

        // One remote blob already present in cache with real content.
        var existingContent = new byte[] { 1, 2, 3, 4, 5 };
        var cachePath = RelativePath.Parse(existingHash.ToString());
        await cacheFileSystem.WriteAllBytesAsync(cachePath, existingContent, CancellationToken.None);

        blobs.SeedBlob(BlobPaths.FileTreePath(existingHash), [], contentType: null);

        await fixture.FileTreeService.ValidateAsync();

        // Existing file content must be preserved, not overwritten with an empty marker.
        var after = await cacheFileSystem.ReadAllBytesAsync(cachePath, CancellationToken.None);
        after.ShouldBe(existingContent);
    }

    // ── 7.8  ValidateAsync — no local markers — slow path triggered ───────────

    [Test]
    public async Task ValidateAsync_NoLocalMarkers_SlowPathTriggered()
    {
        const string acct = "tc-val-noloc", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);
        var cacheFileSystem = new RelativeFileSystem(fixture.FileTreeCacheDirectory);
        var remoteHash = FakeFileTreeHash('4');

        // No local snapshot markers; snapshot cache directory is empty.

        // Remote snapshot exists.
        blobs.SeedBlob(BlobPaths.SnapshotPath("2024-06-15T100000.000Z"), [], contentType: null);
        // Remote filetree blob.
        blobs.SeedBlob(BlobPaths.FileTreePath(remoteHash), [], contentType: null);

        await fixture.FileTreeService.ValidateAsync();

        // Slow path should create a marker for the remote blob.
        cacheFileSystem.FileExists(RelativePath.Parse(remoteHash.ToString())).ShouldBeTrue();
    }

    // ── 7.9  ValidateAsync — no remote snapshots — fast path (empty repo) ─────

    [Test]
    public async Task ValidateAsync_NoRemoteSnapshots_FastPath()
    {
        const string acct = "tc-val-empty", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);

        // No remote blobs at all; ListAsync returns empty for snapshots.
        await fixture.FileTreeService.ValidateAsync();

        // Should not throw and should be marked validated.
        Should.NotThrow(() => fixture.FileTreeService.ExistsInRemote(FakeFileTreeHash('a')));
    }

    // ── 7.10  ExistsInRemote — File.Exists check ─────────────────────────────

    [Test]
    public async Task ExistsInRemote_ReturnsTrue_WhenDiskFileExists()
    {
        const string acct = "tc-exists-true", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);
        var cacheFileSystem = new RelativeFileSystem(fixture.FileTreeCacheDirectory);

        await fixture.FileTreeService.ValidateAsync();
        var hash = FakeFileTreeHash('a');
        await cacheFileSystem.WriteAllBytesAsync(RelativePath.Parse(hash.ToString()), [], CancellationToken.None);

        fixture.FileTreeService.ExistsInRemote(hash).ShouldBeTrue();
    }

    [Test]
    public async Task ExistsInRemote_ReturnsFalse_WhenDiskFileAbsent()
    {
        const string acct = "tc-exists-false", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);

        await fixture.FileTreeService.ValidateAsync();

        fixture.FileTreeService.ExistsInRemote(FakeFileTreeHash('b')).ShouldBeFalse();
    }

    // ── 7.11  ExistsInRemote — throws before ValidateAsync ───────────────────

    [Test]
    public async Task ExistsInRemote_BeforeValidateAsync_ThrowsInvalidOperationException()
    {
        const string acct = "tc-exists-guard", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);

        Should.Throw<InvalidOperationException>(() => fixture.FileTreeService.ExistsInRemote(FakeFileTreeHash('a')));
    }

    // ── 7.12  SnapshotService.CreateAsync — write-through to disk ────────────

    [Test]
    public async Task SnapshotService_CreateAsync_WritesPlainJsonToDisk()
    {
        const string acct = "tc-marker", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);
        var snapshotsFileSystem = new RelativeFileSystem(fixture.SnapshotCacheDirectory);

        var ts       = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var rootHash = FileTreeHash.Parse("aabbccdd" + new string('0', 56));
        var manifest = await fixture.Snapshot.CreateAsync(rootHash, fileCount: 5, originalSize: 512, timestamp: ts);

        var expectedFileName = ts.UtcDateTime.ToString(SnapshotService.TimestampFormat);
        var localPath        = RelativePath.Parse(expectedFileName);

        snapshotsFileSystem.FileExists(localPath).ShouldBeTrue();
        var json = await snapshotsFileSystem.ReadAllTextAsync(localPath, CancellationToken.None);
        json.ShouldContain(rootHash.ToString());
        blobs.UploadedBlobNames.ShouldContain(BlobPaths.SnapshotPath(ts));
    }

    // ── 7.x  ValidateAsync — idempotent (called twice, no double slow-path) ───

    [Test]
    public async Task ValidateAsync_IsIdempotent_SecondCallIsNoOp()
    {
        const string acct = "tc-val-idempotent", cont = "container";
        var blobs = new FakeInMemoryBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, acct, cont, IEncryptionService.PlaintextInstance);
        var someHash = FakeFileTreeHash('5');

        blobs.SeedBlob(BlobPaths.SnapshotPath("2024-06-15T100000.000Z"), [], contentType: null);
        blobs.SeedBlob(BlobPaths.FileTreePath(someHash), [], contentType: null);

        // Remote snapshot mismatch forces slow path on first call.
        await fixture.FileTreeService.ValidateAsync();

        // Clear tracking data and call again; no new listing should happen.
        blobs.UploadedBlobNames.Clear();
        blobs.RequestedBlobNames.Clear();

        await fixture.FileTreeService.ValidateAsync();

        // No Azure calls on second invocation.
        blobs.RequestedBlobNames.ShouldNotContain(BlobPaths.FileTreesPrefix);
        blobs.UploadedBlobNames.ShouldBeEmpty();
    }

}
