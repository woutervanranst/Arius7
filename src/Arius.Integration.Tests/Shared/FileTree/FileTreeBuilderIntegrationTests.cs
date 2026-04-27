using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Storage;

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

    private static FileTreeBuilder CreateBuilder(IBlobContainerService blobs, string containerName)
    {
        var index = new ChunkIndexService(blobs, s_enc, Account, containerName);
        var fileTreeService = new FileTreeService(blobs, s_enc, index, Account, containerName);
        return new FileTreeBuilder(s_enc, fileTreeService);
    }

    private static ManifestEntry MakeEntry(string path, string hash, DateTimeOffset ts) =>
        new(path, hash, ts, ts);

    // ── Upload and download roundtrip ─────────────────────────────────────────

    [Test]
    public async Task BuildAsync_SingleFile_BlobUploadedToFileTreesPrefix()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();

        var manifestPath = Path.GetTempFileName();
        try
        {
            var now   = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var entry = MakeEntry("readme.txt", "aaa", now);
            await File.WriteAllTextAsync(manifestPath, entry.Serialize() + "\n");

            var builder  = CreateBuilder(blobs, container.Name);
            var rootHash = await builder.BuildAsync(manifestPath);

            rootHash.ShouldNotBeNull();
            var resolvedRootHash = rootHash!.Value;

            // Verify the blob exists in blob storage
            var blobName = BlobPaths.FileTree(resolvedRootHash);
            var meta     = await blobs.GetMetadataAsync(blobName);
            meta.Exists.ShouldBeTrue();

            // Download and deserialize to verify content
            await using var stream = await blobs.DownloadAsync(blobName);
            var treeBlob = await FileTreeBlobSerializer.DeserializeFromStorageAsync(stream, s_enc);

            treeBlob.Entries.Count.ShouldBe(1);
            treeBlob.Entries[0].Name.ShouldBe("readme.txt");
            treeBlob.Entries[0].ShouldBeOfType<FileEntry>().ContentHash.ShouldBe(ContentHash.Parse("aaa"));
        }
        finally
        {
            File.Delete(manifestPath);
            // clean up disk cache
            var cacheDir = FileTreeService.GetDiskCacheDirectory(Account, container.Name);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    // ── Dedup across runs: second run should not re-upload ────────────────────

    [Test]
    public async Task BuildAsync_SecondRun_SameManifest_DoesNotReupload()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();

        var manifestPath = Path.GetTempFileName();
        try
        {
            var now   = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var entry = MakeEntry("photo.jpg", "bbb", now);
            await File.WriteAllTextAsync(manifestPath, entry.Serialize() + "\n");

            var builder1 = CreateBuilder(blobs, container.Name);
            var root1    = await builder1.BuildAsync(manifestPath);

            // Count blobs in filetrees/ after first run
            var blobsAfterRun1 = new List<string>();
            await foreach (var b in blobs.ListAsync(BlobPaths.FileTrees))
                blobsAfterRun1.Add(b);

            // Second run with same manifest
            var builder2 = CreateBuilder(blobs, container.Name);
            var root2    = await builder2.BuildAsync(manifestPath);

            root2.ShouldBe(root1);

            // Blob count should be the same (no new blobs)
            var blobsAfterRun2 = new List<string>();
            await foreach (var b in blobs.ListAsync(BlobPaths.FileTrees))
                blobsAfterRun2.Add(b);

            blobsAfterRun2.Count.ShouldBe(blobsAfterRun1.Count);
        }
        finally
        {
            File.Delete(manifestPath);
            var cacheDir = FileTreeService.GetDiskCacheDirectory(Account, container.Name);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    // ── Nested directories produce correct tree hierarchy ─────────────────────

    [Test]
    public async Task BuildAsync_NestedDirectories_ProducesCorrectTree()
    {
        var (container, blobs) = await azurite.CreateTestServiceAsync();

        var manifestPath = Path.GetTempFileName();
        try
        {
            var now   = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero);
            var lines = new[]
            {
                MakeEntry("photos/2024/june/a.jpg", "ccc", now).Serialize(),
                MakeEntry("photos/2024/june/b.jpg", "ddd", now).Serialize(),
                MakeEntry("docs/report.pdf",        "eee", now).Serialize(),
            };
            await File.WriteAllTextAsync(manifestPath, string.Join("\n", lines) + "\n");

            var builder  = CreateBuilder(blobs, container.Name);
            var rootHash = await builder.BuildAsync(manifestPath);

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
            File.Delete(manifestPath);
            var cacheDir = FileTreeService.GetDiskCacheDirectory(Account, container.Name);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }
}
