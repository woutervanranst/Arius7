using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;
using Shouldly;

namespace Arius.Core.Tests.FileTree;

/// <summary>
/// Unit tests for <see cref="TreeCacheService"/> covering tasks 7.1–7.12.
/// Each test uses isolated account/container names to avoid cross-test disk pollution,
/// and cleans up the cache directories in a finally block.
/// </summary>
public class TreeCacheServiceTests
{
    private static readonly PlaintextPassthroughService s_enc = new();

    private static readonly DateTimeOffset s_ts1 = new(2024, 1, 1,  0,  0,  0,  TimeSpan.Zero);
    private static readonly DateTimeOffset s_ts2 = new(2024, 6, 15, 10, 0,  0,  TimeSpan.Zero);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TreeBlob MakeBlob(string fileName = "a.txt", string hash = "aabbccdd") =>
        new()
        {
            Entries =
            [
                new TreeEntry
                {
                    Name     = fileName,
                    Type     = TreeEntryType.File,
                    Hash     = hash,
                    Created  = s_ts1,
                    Modified = s_ts2
                }
            ]
        };

    private static (TreeCacheService svc, FakeInMemoryBlobContainerService blobs, string cacheDir, string snapshotsDir)
        MakeService(string acct, string cont)
    {
        var blobs       = new FakeInMemoryBlobContainerService();
        var svc         = new TreeCacheService(blobs, s_enc, acct, cont);
        var cacheDir    = TreeCacheService.GetDiskCacheDirectory(acct, cont);
        var snapshotsDir = TreeCacheService.GetSnapshotsDirectory(acct, cont);
        return (svc, blobs, cacheDir, snapshotsDir);
    }

    private static async Task CleanupAsync(string cacheDir, string snapshotsDir)
    {
        if (Directory.Exists(cacheDir))    Directory.Delete(cacheDir,     recursive: true);
        if (Directory.Exists(snapshotsDir)) Directory.Delete(snapshotsDir, recursive: true);
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
            var blob     = MakeBlob();
            var hash     = TreeBlobSerializer.ComputeHash(blob, s_enc);
            var diskPath = Path.Combine(cacheDir, hash);

            // Pre-populate disk cache with plaintext
            var plaintext = TreeBlobSerializer.Serialize(blob);
            await File.WriteAllBytesAsync(diskPath, plaintext);

            var result = await svc.ReadAsync(hash);

            result.Entries.Count.ShouldBe(1);
            result.Entries[0].Name.ShouldBe("a.txt");
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
            var blob     = MakeBlob("photo.jpg", "deadbeef");
            var hash     = TreeBlobSerializer.ComputeHash(blob, s_enc);
            var blobName = BlobPaths.FileTree(hash);

            // Pre-populate Azure (storage serialization)
            var storageBytes = await TreeBlobSerializer.SerializeForStorageAsync(blob, s_enc);
            blobs.SeedBlob(blobName, storageBytes, contentType: ContentTypes.FileTreePlaintext);
            blobs.RequestedBlobNames.Clear(); // clear seed bookkeeping

            var result = await svc.ReadAsync(hash);

            result.Entries.Count.ShouldBe(1);
            result.Entries[0].Name.ShouldBe("photo.jpg");

            // Azure was called
            blobs.RequestedBlobNames.ShouldContain(blobName);

            // Disk file was written (write-through)
            var diskPath = Path.Combine(cacheDir, hash);
            File.Exists(diskPath).ShouldBeTrue();
            (await File.ReadAllBytesAsync(diskPath)).Length.ShouldBeGreaterThan(0);
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
            var blob = MakeBlob("doc.pdf", "cafebabe");
            var hash = TreeBlobSerializer.ComputeHash(blob, s_enc);

            await svc.WriteAsync(hash, blob);

            // Azure was uploaded
            var blobName = BlobPaths.FileTree(hash);
            blobs.UploadedBlobNames.ShouldContain(blobName);

            // Disk file was written
            var diskPath = Path.Combine(cacheDir, hash);
            File.Exists(diskPath).ShouldBeTrue();
            (await File.ReadAllBytesAsync(diskPath)).Length.ShouldBeGreaterThan(0);
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
            var blob     = MakeBlob();
            var hash     = TreeBlobSerializer.ComputeHash(blob, s_enc);
            var blobName = BlobPaths.FileTree(hash);

            // Seed blob in Azure so upload throws BlobAlreadyExistsException
            var storageBytes = await TreeBlobSerializer.SerializeForStorageAsync(blob, s_enc);
            blobs.SeedBlob(blobName, storageBytes, contentType: ContentTypes.FileTreePlaintext);

            // Should not throw
            await Should.NotThrowAsync(() => svc.WriteAsync(hash, blob));

            // Disk file should still be written
            var diskPath = Path.Combine(cacheDir, hash);
            File.Exists(diskPath).ShouldBeTrue();
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
        }
    }

    // ── 7.5  ValidateAsync — snapshot match — no filetrees listing ────────────

    [Test]
    public async Task ValidateAsync_SnapshotMatch_NoFiletreesListing()
    {
        const string acct = "tc-val-match", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
        try
        {
            var timestamp = "2024-06-15T100000.000Z";

            // Write a local snapshot marker
            await File.WriteAllBytesAsync(Path.Combine(snapshotsDir, timestamp), []);

            // Seed a remote snapshot with the same timestamp
            blobs.SeedBlob(BlobPaths.Snapshot(timestamp), [], contentType: null);

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

    // ── 7.6  ValidateAsync — mismatch — creates markers + deletes L2 ──────────

    [Test]
    public async Task ValidateAsync_SnapshotMismatch_MarkerFilesCreated_L2Deleted()
    {
        const string acct = "tc-val-miss", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);

        // Determine the L2 dir and pre-populate it with a dummy file
        var chunkL2Dir = Arius.Core.Shared.ChunkIndex.ChunkIndexService.GetL2Directory(acct, cont);
        Directory.CreateDirectory(chunkL2Dir);
        var dummyL2File = Path.Combine(chunkL2Dir, "dummy-shard.dat");
        await File.WriteAllTextAsync(dummyL2File, "stale");

        try
        {
            // Local marker: old snapshot
            await File.WriteAllBytesAsync(Path.Combine(snapshotsDir, "2024-01-01T000000.000Z"), []);

            // Remote snapshot: newer
            blobs.SeedBlob(BlobPaths.Snapshot("2024-06-15T100000.000Z"), [], contentType: null);

            // Two remote filetree blobs
            blobs.SeedBlob(BlobPaths.FileTree("hash1111"), [], contentType: null);
            blobs.SeedBlob(BlobPaths.FileTree("hash2222"), [], contentType: null);

            await svc.ValidateAsync();

            // Empty marker files created for remote filetrees
            File.Exists(Path.Combine(cacheDir, "hash1111")).ShouldBeTrue();
            File.Exists(Path.Combine(cacheDir, "hash2222")).ShouldBeTrue();

            // L2 dummy file deleted
            File.Exists(dummyL2File).ShouldBeFalse();
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
            if (Directory.Exists(chunkL2Dir)) Directory.Delete(chunkL2Dir, recursive: true);
        }
    }

    // ── 7.7  ValidateAsync — mismatch does NOT overwrite existing cache files ──

    [Test]
    public async Task ValidateAsync_Mismatch_DoesNotOverwriteExistingCacheFiles()
    {
        const string acct = "tc-val-noover", cont = "container";
        var (svc, blobs, cacheDir, snapshotsDir) = MakeService(acct, cont);
        try
        {
            // Local marker: old snapshot
            await File.WriteAllBytesAsync(Path.Combine(snapshotsDir, "2024-01-01T000000.000Z"), []);

            // Remote snapshot: newer
            blobs.SeedBlob(BlobPaths.Snapshot("2024-06-15T100000.000Z"), [], contentType: null);

            // One remote blob already present in cache with real content
            var existingContent = new byte[] { 1, 2, 3, 4, 5 };
            var diskPath = Path.Combine(cacheDir, "existinghash");
            await File.WriteAllBytesAsync(diskPath, existingContent);

            blobs.SeedBlob(BlobPaths.FileTree("existinghash"), [], contentType: null);

            await svc.ValidateAsync();

            // Existing file content must be preserved (not overwritten with empty marker)
            var after = await File.ReadAllBytesAsync(diskPath);
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
        try
        {
            // No local snapshot markers (snapshotsDir is empty)

            // Remote snapshot exists
            blobs.SeedBlob(BlobPaths.Snapshot("2024-06-15T100000.000Z"), [], contentType: null);

            // Remote filetree blob
            blobs.SeedBlob(BlobPaths.FileTree("remotehash"), [], contentType: null);

            await svc.ValidateAsync();

            // Slow path should create marker for remote blob
            File.Exists(Path.Combine(cacheDir, "remotehash")).ShouldBeTrue();
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
            Should.NotThrow(() => svc.ExistsInRemote("anyhash"));
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
        try
        {
            await svc.ValidateAsync();
            var diskPath = Path.Combine(cacheDir, "knowhash");
            await File.WriteAllBytesAsync(diskPath, []);

            svc.ExistsInRemote("knowhash").ShouldBeTrue();
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

            svc.ExistsInRemote("nohash").ShouldBeFalse();
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
            Should.Throw<InvalidOperationException>(() => svc.ExistsInRemote("anyhash"));
        }
        finally
        {
            if (Directory.Exists(cacheDir))    Directory.Delete(cacheDir,     recursive: true);
            if (Directory.Exists(snapshotsDir)) Directory.Delete(snapshotsDir, recursive: true);
        }
    }

    // ── 7.12  WriteSnapshotMarkerAsync ────────────────────────────────────────

    [Test]
    public async Task WriteSnapshotMarkerAsync_CreatesEmptyFileWithCorrectName()
    {
        const string acct = "tc-marker", cont = "container";
        var (svc, _, cacheDir, snapshotsDir) = MakeService(acct, cont);
        try
        {
            var timestamp = "2024-06-15T100000.000Z";
            await svc.WriteSnapshotMarkerAsync(timestamp);

            var markerPath = Path.Combine(snapshotsDir, timestamp);
            File.Exists(markerPath).ShouldBeTrue();
            (await File.ReadAllBytesAsync(markerPath)).Length.ShouldBe(0);
        }
        finally
        {
            await CleanupAsync(cacheDir, snapshotsDir);
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
            // Remote snapshot mismatch forces slow path on first call
            blobs.SeedBlob(BlobPaths.Snapshot("2024-06-15T100000.000Z"), [], contentType: null);
            blobs.SeedBlob(BlobPaths.FileTree("somehash"), [], contentType: null);

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
