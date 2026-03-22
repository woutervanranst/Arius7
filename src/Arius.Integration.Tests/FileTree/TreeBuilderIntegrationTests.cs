using Arius.Core.Encryption;
using Arius.Core.FileTree;
using Arius.Core.Storage;
using Arius.Integration.Tests.Storage;
using Shouldly;

namespace Arius.Integration.Tests.FileTree;

/// <summary>
/// Integration tests for <see cref="TreeBuilder"/> against a real Azurite blob service.
/// Requires Docker (Azurite via TestContainers). Task 5.11.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class TreeBuilderIntegrationTests(AzuriteFixture azurite)
{
    private const string Account = "devstoreaccount1";
    private static readonly PlaintextPassthroughService s_enc = new();

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
            var entry = MakeEntry("readme.txt", "aabbccdd", now);
            await File.WriteAllTextAsync(manifestPath, entry.Serialize() + "\n");

            var builder  = new TreeBuilder(blobs, s_enc, Account, container.Name);
            var rootHash = await builder.BuildAsync(manifestPath);

            rootHash.ShouldNotBeNull();

            // Verify the blob exists in blob storage
            var blobName = BlobPaths.FileTree(rootHash!);
            var meta     = await blobs.GetMetadataAsync(blobName);
            meta.Exists.ShouldBeTrue();

            // Download and deserialize to verify content
            await using var stream = await blobs.DownloadAsync(blobName);
            var treeBlob = await TreeBlobSerializer.DeserializeAsync(stream);

            treeBlob.Entries.Count.ShouldBe(1);
            treeBlob.Entries[0].Name.ShouldBe("readme.txt");
            treeBlob.Entries[0].Hash.ShouldBe("aabbccdd");
        }
        finally
        {
            File.Delete(manifestPath);
            // clean up disk cache
            var cacheDir = TreeBuilder.GetDiskCacheDirectory(Account, container.Name);
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
            var entry = MakeEntry("photo.jpg", "deadbeef", now);
            await File.WriteAllTextAsync(manifestPath, entry.Serialize() + "\n");

            var builder1 = new TreeBuilder(blobs, s_enc, Account, container.Name);
            var root1    = await builder1.BuildAsync(manifestPath);

            // Count blobs in filetrees/ after first run
            var blobsAfterRun1 = new List<string>();
            await foreach (var b in blobs.ListAsync(BlobPaths.FileTrees))
                blobsAfterRun1.Add(b);

            // Second run with same manifest
            var builder2 = new TreeBuilder(blobs, s_enc, Account, container.Name);
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
            var cacheDir = TreeBuilder.GetDiskCacheDirectory(Account, container.Name);
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
                MakeEntry("photos/2024/june/a.jpg", "hash_a", now).Serialize(),
                MakeEntry("photos/2024/june/b.jpg", "hash_b", now).Serialize(),
                MakeEntry("docs/report.pdf",         "hash_r", now).Serialize(),
            };
            await File.WriteAllTextAsync(manifestPath, string.Join("\n", lines) + "\n");

            var builder  = new TreeBuilder(blobs, s_enc, Account, container.Name);
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
            var cacheDir = TreeBuilder.GetDiskCacheDirectory(Account, container.Name);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }
}
