using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Tests.Shared.Snapshot.Fakes;
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
        blobs.SeedBlob(
            BlobPaths.ChunkPath(parentChunkHash),
            [4, 5],
            BlobTier.Archive,
            new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeTar });
        blobs.SeedBlob(staleShard, [9], BlobTier.Cool);
        using var index = CreateIndex(blobs, "repair-rebuild");

        var result = await index.RepairAsync();

        result.ListedChunkCount.ShouldBe(3);
        result.RebuiltEntryCount.ShouldBe(2);
        result.UploadedShardCount.ShouldBe(2);
        result.DeletedStaleShardCount.ShouldBe(1);
        blobs.DeletedBlobNames.ShouldContain(staleShard);

        // The large entry's tier comes from its own blob; the thin entry's from its parent tar.
        (await index.LookupAsync(largeContentHash)).ShouldBe(new ShardEntry(largeContentHash, ChunkHash.Parse(largeContentHash), 100, 3, BlobTier.Cool));
        (await index.LookupAsync(thinContentHash)).ShouldBe(new ShardEntry(thinContentHash, parentChunkHash, 10, 2, BlobTier.Archive));
    }

    [Test]
    public async Task RepairAsync_WritesRepairMarker_RecreatesSqliteCache_AndStagesEntriesInSqlite()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("repair-sqlite-staging");
        var cache = new RelativeFileSystem(RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey));
        cache.CreateDirectory(RelativePath.Root);

        blobs.SeedBlob(
            BlobPaths.ChunkPath(FakeChunkHash('a')),
            [1, 2, 3],
            BlobTier.Cool,
            new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeLarge,
                [BlobMetadataKeys.OriginalSize] = "100",
                [BlobMetadataKeys.ChunkSize] = "3",
            });

        var staleStore = new ChunkIndexLocalStore(RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey));
        staleStore.UpsertPendingFlush(new ShardEntry(FakeContentHash('f'), FakeChunkHash('e'), 1, 1, BlobTier.Cool));

        using var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);

        var result = await index.RepairAsync();

        result.RebuiltEntryCount.ShouldBe(1);
        cache.FileExists(RelativePath.Parse("cache.sqlite.bak")).ShouldBeTrue();
        cache.FileExists(RelativePath.Parse("cache.sqlite")).ShouldBeTrue();
        var repository = new RelativeFileSystem(RepositoryLocalStatePaths.GetRepositoryRoot(repositoryKey, repositoryKey));
        repository.FileExists(ChunkIndexService.RepairInProgressMarkerPath).ShouldBeFalse();
    }

    [Test]
    public async Task RepairAsync_ReplacesRebuiltPrefixInsteadOfMergingStaleShardContents()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("repair-replace-prefix");
        var rebuiltHash = FakeContentHash('a');
        var staleHash = ContentHash.Parse($"{rebuiltHash.Prefix(ChunkIndexService.ShardPrefixLength)}{new string('f', 64 - ChunkIndexService.ShardPrefixLength)}");
        var staleShard = new Shard();
        staleShard.AddOrUpdate(new ShardEntry(staleHash, FakeChunkHash('f'), 999, 111, BlobTier.Cool));
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(rebuiltHash)),
            await ShardSerializer.SerializeAsync(staleShard, s_encryption),
            BlobTier.Cool);
        blobs.SeedBlob(
            BlobPaths.ChunkPath(ChunkHash.Parse(rebuiltHash)),
            [1, 2, 3],
            BlobTier.Cool,
            new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeLarge,
                [BlobMetadataKeys.OriginalSize] = "100",
                [BlobMetadataKeys.ChunkSize] = "3",
            });
        using var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);

        var result = await index.RepairAsync();

        result.RebuiltShardCount.ShouldBe(1);
        (await index.LookupAsync(rebuiltHash)).ShouldBe(new ShardEntry(rebuiltHash, ChunkHash.Parse(rebuiltHash), 100, 3, BlobTier.Cool));
        (await index.LookupAsync(staleHash)).ShouldBeNull();
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
        using var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);

        await Should.ThrowAsync<ChunkIndexRepairException>(() => index.RepairAsync());

        var repository = new RelativeFileSystem(RepositoryLocalStatePaths.GetRepositoryRoot(repositoryKey, repositoryKey));
        repository.FileExists(ChunkIndexService.RepairInProgressMarkerPath).ShouldBeTrue();
    }

    [Test]
    public async Task RepairAsync_ClearsPendingAndInFlightEntries()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("repair-clears-memory");
        var staleContentHash = FakeContentHash('a');
        var staleEntry = new ShardEntry(staleContentHash, FakeChunkHash('b'), 10, 2, BlobTier.Cool);
        using var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);
        index.AddEntry(staleEntry);

        await index.RepairAsync();
        await index.FlushAsync();

        using var resumedIndex = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);
        (await resumedIndex.LookupAsync(staleContentHash)).ShouldBeNull();
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
        var store = new ChunkIndexLocalStore(RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey));
        repository.CreateDirectory(RelativePath.Root);
        cache.CreateDirectory(RelativePath.Root);
        await repository.WriteAllBytesAsync(ChunkIndexService.RepairInProgressMarkerPath, [], CancellationToken.None);
        store.UpsertPendingFlush(new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool));
        using var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);

        var result = await index.RepairAsync();

        result.RebuiltEntryCount.ShouldBe(0);
        repository.FileExists(ChunkIndexService.RepairInProgressMarkerPath).ShouldBeFalse();
        cache.FileExists(RelativePath.Parse("cache.sqlite.bak")).ShouldBeTrue();
        cache.FileExists(RelativePath.Parse("cache.sqlite")).ShouldBeTrue();
    }

    private static ChunkIndexService CreateIndex(FakeInMemoryBlobContainerService blobs, string name)
    {
        var repositoryKey = UniqueRepositoryKey(name);
        return new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey);
    }

    private static string UniqueRepositoryKey(string name) => $"acct-{name}-{Guid.NewGuid():N}";
}
