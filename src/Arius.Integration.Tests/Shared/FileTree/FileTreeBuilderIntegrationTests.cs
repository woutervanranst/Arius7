using System.IO.Compression;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Fixtures;

namespace Arius.Integration.Tests.Shared.FileTree;

/// <summary>
/// Integration tests for <see cref="FileTreeBuilder"/> against a real Azurite blob service.
/// Requires Docker (Azurite via TestContainers). Task 5.11.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class FileTreeBuilderIntegrationTests(AzuriteFixture azurite)
{
    private const string Account = "devstoreaccount1";
    private static readonly PlaintextPassthroughService s_enc = new();

    private static FileTreeBuilder CreateBuilder(
        IBlobContainerService blobs,
        string containerName,
        out FileTreeService fileTreeService)
    {
        var index = new ChunkIndexService(blobs, s_enc, Account, containerName);
        fileTreeService = new FileTreeService(blobs, s_enc, index, Account, containerName);
        return new FileTreeBuilder(s_enc, fileTreeService);
    }

    private static async Task<FileTreeStagingSession> CreateStagingAsync(
        string containerName,
        params (string Path, ContentHash Hash, DateTimeOffset Timestamp)[] files)
    {
        var cacheDir = FileTreeService.GetDiskCacheDirectory(Account, containerName);
        var session = await FileTreeStagingSession.OpenAsync(cacheDir);
        using var writer = new FileTreeStagingWriter(session.StagingRoot);
        foreach (var file in files)
            await writer.AppendFileEntryAsync(file.Path, file.Hash, file.Timestamp, file.Timestamp);

        return session;
    }

    // ── Upload and download roundtrip ─────────────────────────────────────────

    [Test]
    public async Task SynchronizeAsync_SingleFile_BlobUploadedToFileTreesPrefix()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();

        try
        {
            var now   = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            await using var stagingSession = await CreateStagingAsync(
                container.Name,
                ("readme.txt", FakeContentHash('a'), now));

            var builder  = CreateBuilder(blobs, container.Name, out var fileTreeService);
            await fileTreeService.ValidateAsync();
            var rootHash = await builder.SynchronizeAsync(stagingSession.StagingRoot);

            rootHash.ShouldNotBeNull();
            var resolvedRootHash = rootHash!.Value;

            // Verify the blob exists in blob storage
            var blobName = BlobPaths.FileTree(resolvedRootHash);
            var meta     = await blobs.GetMetadataAsync(blobName);
            meta.Exists.ShouldBeTrue();

            // Download and deserialize to verify content
            await using var stream = await blobs.DownloadAsync(blobName);
            var entries = await ReadStoredTreeAsync(stream, s_enc);

            entries.Count.ShouldBe(1);
            entries[0].Name.ShouldBe("readme.txt");
            entries[0].ShouldBeOfType<FileEntry>().ContentHash.ShouldBe(FakeContentHash('a'));
        }
        finally
        {
            // clean up disk cache
            var cacheDir = FileTreeService.GetDiskCacheDirectory(Account, container.Name);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    // ── Dedup across runs: second run should not re-upload ────────────────────

    [Test]
    public async Task SynchronizeAsync_SecondRun_SameManifest_DoesNotReupload()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();

        try
        {
            var now   = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            await using var stagingSession = await CreateStagingAsync(
                container.Name,
                ("photo.jpg", FakeContentHash('b'), now));

            var builder1 = CreateBuilder(blobs, container.Name, out var fileTreeService1);
            await fileTreeService1.ValidateAsync();
            var root1    = await builder1.SynchronizeAsync(stagingSession.StagingRoot);

            // Count blobs in filetrees/ after first run
            var blobsAfterRun1 = new List<string>();
            await foreach (var b in blobs.ListAsync(BlobPaths.FileTrees))
                blobsAfterRun1.Add(b);

            // Second run with same manifest
            var builder2 = CreateBuilder(blobs, container.Name, out var fileTreeService2);
            await fileTreeService2.ValidateAsync();
            var root2    = await builder2.SynchronizeAsync(stagingSession.StagingRoot);

            root2.ShouldBe(root1);

            // Blob count should be the same (no new blobs)
            var blobsAfterRun2 = new List<string>();
            await foreach (var b in blobs.ListAsync(BlobPaths.FileTrees))
                blobsAfterRun2.Add(b);

            blobsAfterRun2.Count.ShouldBe(blobsAfterRun1.Count);
        }
        finally
        {
            var cacheDir = FileTreeService.GetDiskCacheDirectory(Account, container.Name);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    // ── Nested directories produce correct tree hierarchy ─────────────────────

    [Test]
    public async Task SynchronizeAsync_NestedDirectories_ProducesCorrectTree()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();

        try
        {
            var now   = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            await using var stagingSession = await CreateStagingAsync(
                container.Name,
                ("photos/2024/june/a.jpg", FakeContentHash('c'), now),
                ("photos/2024/june/b.jpg", FakeContentHash('d'), now),
                ("docs/report.pdf", FakeContentHash('e'), now));

            var builder  = CreateBuilder(blobs, container.Name, out var fileTreeService);
            await fileTreeService.ValidateAsync();
            var rootHash = await builder.SynchronizeAsync(stagingSession.StagingRoot);

            rootHash.ShouldNotBeNull();

            // Multiple tree blobs should have been uploaded (one per directory + root)
            var treeBlobNames = new List<string>();
            await foreach (var b in blobs.ListAsync(BlobPaths.FileTrees))
                treeBlobNames.Add(b);

            // Expected: filetrees/ for june, 2024, photos, docs, root = at least 4
            treeBlobNames.Count.ShouldBeGreaterThanOrEqualTo(4);
        }
        finally
        {
            var cacheDir = FileTreeService.GetDiskCacheDirectory(Account, container.Name);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    private static async Task<IReadOnlyList<FileTreeEntry>> ReadStoredTreeAsync(Stream source, IEncryptionService encryption)
    {
        await using var decStream = encryption.WrapForDecryption(source);
        await using var gzipStream = new GZipStream(decStream, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        await gzipStream.CopyToAsync(ms);
        return FileTreeSerializer.Deserialize(ms.ToArray());
    }
}
