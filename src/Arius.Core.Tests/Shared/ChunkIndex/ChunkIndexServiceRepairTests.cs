using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ChunkIndexServiceRepairTests
{
    private static readonly PlaintextPassthroughService s_encryption = new();

    [Test]
    public async Task RepairAsync_RebuildsLargeAndThinEntriesAndDeletesStaleShards()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var largeContentHash = FakeContentHash('a');
        var thinContentHash = FakeContentHash('b');
        var parentChunkHash = FakeChunkHash('c');
        var staleShard = BlobPaths.ChunkIndexShardPath(PathSegment.Parse("ff"));

        blobs.SeedBlob(
            BlobPaths.ChunkPath(ChunkHash.Parse(largeContentHash)),
            [1, 2, 3],
            BlobTier.Cool,
            new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeLarge,
                [BlobMetadataKeys.OriginalSize] = "100",
                [BlobMetadataKeys.ChunkSize] = "3",
            });
        blobs.SeedBlob(
            BlobPaths.ThinChunkPath(thinContentHash),
            [],
            BlobTier.Cool,
            new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeThin,
                [BlobMetadataKeys.ParentChunkHash] = parentChunkHash.ToString(),
                [BlobMetadataKeys.OriginalSize] = "10",
                [BlobMetadataKeys.CompressedSize] = "2",
            });
        blobs.SeedBlob(staleShard, [9], BlobTier.Cool);
        using var index = CreateIndex(blobs, "repair-rebuild");

        var result = await index.RepairAsync();

        result.ListedChunkCount.ShouldBe(2);
        result.RebuiltEntryCount.ShouldBe(2);
        result.UploadedShardCount.ShouldBe(2);
        result.DeletedStaleShardCount.ShouldBe(1);
        blobs.DeletedBlobNames.ShouldContain(staleShard);

        (await index.LookupAsync(largeContentHash)).ShouldBe(new ShardEntry(largeContentHash, ChunkHash.Parse(largeContentHash), 100, 3));
        (await index.LookupAsync(thinContentHash)).ShouldBe(new ShardEntry(thinContentHash, parentChunkHash, 10, 2));
    }

    [Test]
    public async Task RepairAsync_InvalidThinMetadata_FailsAndKeepsRepairMarker()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("repair-invalid-thin");
        var thinContentHash = FakeContentHash('b');
        blobs.SeedBlob(
            BlobPaths.ThinChunkPath(thinContentHash),
            [],
            BlobTier.Cool,
            new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeThin,
                [BlobMetadataKeys.OriginalSize] = "10",
                [BlobMetadataKeys.CompressedSize] = "2",
            });
        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);

        await Should.ThrowAsync<ChunkIndexRepairException>(() => index.RepairAsync());

        var repository = new RelativeFileSystem(RepositoryLocalStatePaths.GetRepositoryRoot(repositoryKey, repositoryKey));
        repository.FileExists(ChunkIndexService.RepairInProgressMarkerPath).ShouldBeTrue();
    }

    [Test]
    public async Task RepairAsync_ClearsPendingAndInFlightEntries()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var staleContentHash = FakeContentHash('a');
        var staleEntry = new ShardEntry(staleContentHash, FakeChunkHash('b'), 10, 2);
        using var index = CreateIndex(blobs, "repair-clears-memory");
        index.AddEntry(staleEntry);

        await index.RepairAsync();
        await index.FlushAsync();

        (await index.LookupAsync(staleContentHash)).ShouldBeNull();
        blobs.UploadedBlobNames.ShouldBeEmpty();
    }

    [Test]
    public async Task RepairAsync_IgnoresTarAndUnknownChunks()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        blobs.SeedBlob(
            BlobPaths.ChunkPath(FakeChunkHash('d')),
            [1],
            BlobTier.Cool,
            new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeTar });
        blobs.SeedBlob(BlobPaths.ChunkPath(FakeChunkHash('e')), [2], BlobTier.Cool);
        using var index = CreateIndex(blobs, "repair-ignore");

        var result = await index.RepairAsync();

        result.ListedChunkCount.ShouldBe(2);
        result.RebuiltEntryCount.ShouldBe(0);
        result.UploadedShardCount.ShouldBe(0);
    }

    [Test]
    public async Task RepairAsync_RerunPurgesPartialLocalCacheAndCompletes()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("repair-rerun");
        var repository = new RelativeFileSystem(RepositoryLocalStatePaths.GetRepositoryRoot(repositoryKey, repositoryKey));
        var cache = new RelativeFileSystem(RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey));
        repository.CreateDirectory(RelativePath.Root);
        cache.CreateDirectory(RelativePath.Root);
        await repository.WriteAllBytesAsync(ChunkIndexService.RepairInProgressMarkerPath, [], CancellationToken.None);
        await cache.WriteAllBytesAsync(RelativePath.Root / PathSegment.Parse("aa"), [1, 2, 3], CancellationToken.None);
        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);

        var result = await index.RepairAsync();

        result.RebuiltEntryCount.ShouldBe(0);
        repository.FileExists(ChunkIndexService.RepairInProgressMarkerPath).ShouldBeFalse();
        cache.FileExists(RelativePath.Root / PathSegment.Parse("aa")).ShouldBeFalse();
    }

    private static ChunkIndexService CreateIndex(FakeInMemoryBlobContainerService blobs, string name)
    {
        var repositoryKey = UniqueRepositoryKey(name);
        return new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);
    }

    private static string UniqueRepositoryKey(string name) => $"acct-{name}-{Guid.NewGuid():N}";
}
