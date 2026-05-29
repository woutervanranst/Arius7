using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ChunkIndexServiceLookupTests
{
    private static readonly PlaintextPassthroughService s_encryption = new();

    [Test]
    public async Task LookupAsync_MissingRemoteShard_ReturnsMiss()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        using var index = CreateIndex(blobs, "missing");

        var actual = await index.LookupAsync(FakeContentHash('a'));

        actual.ShouldBeNull();
        blobs.RequestedBlobNames.ShouldNotContain(BlobPaths.ChunksPrefix);
    }

    [Test]
    public async Task LookupAsync_CorruptRemoteShard_ThrowsChunkIndexCorruptException()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var contentHash = FakeContentHash('a');
        var shardBlobName = BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash));
        blobs.SeedBlob(shardBlobName, [1, 2, 3], BlobTier.Cool);
        using var index = CreateIndex(blobs, "corrupt");

        var ex = await Should.ThrowAsync<ChunkIndexCorruptException>(() => index.LookupAsync(contentHash));

        ex.Message.ShouldContain("Run the explicit chunk-index repair command");
        ex.ShardBlobName.ShouldBe(shardBlobName);
    }

    [Test]
    public async Task LookupAsync_CorruptLocalShard_DeletesLocalCacheAndReloadsRemoteShard()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("local-corrupt");
        var contentHash = FakeContentHash('a');
        var entry = new ShardEntry(contentHash, FakeChunkHash('b'), 10, 5);
        var shard = new Shard().Merge([entry]);
        var shardBlobName = BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(contentHash));
        blobs.SeedBlob(shardBlobName, await ShardSerializer.SerializeAsync(shard, s_encryption), BlobTier.Cool);

        var cacheRoot = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey);
        var cache = new RelativeFileSystem(cacheRoot);
        cache.CreateDirectory(RelativePath.Root);
        await cache.WriteAllBytesAsync(RelativePath.Root / Shard.PrefixOf(contentHash), [1, 2, 3], CancellationToken.None);

        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);

        var actual = await index.LookupAsync(contentHash);

        actual.ShouldBe(entry);
    }

    [Test]
    public async Task LookupAsync_ValidShardMissingEntry_ReturnsMissWithoutRepair()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var existingHash = FakeContentHash('a');
        var missingHash = ContentHash.Parse($"{existingHash.Prefix(ChunkIndexService.ShardPrefixLength)}{new string('b', 64 - ChunkIndexService.ShardPrefixLength)}");
        var shard = new Shard().Merge([new ShardEntry(existingHash, FakeChunkHash('c'), 10, 5)]);
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(missingHash)),
            await ShardSerializer.SerializeAsync(shard, s_encryption),
            BlobTier.Cool);
        using var index = CreateIndex(blobs, "valid-miss");

        var actual = await index.LookupAsync(missingHash);

        actual.ShouldBeNull();
        blobs.UploadedBlobNames.ShouldBeEmpty();
    }

    [Test]
    public async Task LookupAsync_RepairMarkerExists_ThrowsRepairIncompleteException()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("repair-marker");
        var repository = new RelativeFileSystem(RepositoryLocalStatePaths.GetRepositoryRoot(repositoryKey, repositoryKey));
        repository.CreateDirectory(RelativePath.Root);
        await repository.WriteAllBytesAsync(ChunkIndexService.RepairInProgressMarkerPath, [], CancellationToken.None);
        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);

        var ex = await Should.ThrowAsync<ChunkIndexRepairIncompleteException>(() => index.LookupAsync(FakeContentHash('a')));

        ex.Message.ShouldContain("Rerun the explicit chunk-index repair command");
    }

    [Test]
    public void AddEntry_RepairMarkerExists_ThrowsRepairIncompleteException()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("repair-marker-add");
        var repository = new RelativeFileSystem(RepositoryLocalStatePaths.GetRepositoryRoot(repositoryKey, repositoryKey));
        repository.CreateDirectory(RelativePath.Root);
        repository.WriteAllBytesAsync(ChunkIndexService.RepairInProgressMarkerPath, [], CancellationToken.None).GetAwaiter().GetResult();
        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);

        Should.Throw<ChunkIndexRepairIncompleteException>(() => index.AddEntry(new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5)));
    }

    [Test]
    public async Task FlushAsync_RepairMarkerExists_ThrowsRepairIncompleteException()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("repair-marker-flush");
        var repository = new RelativeFileSystem(RepositoryLocalStatePaths.GetRepositoryRoot(repositoryKey, repositoryKey));
        repository.CreateDirectory(RelativePath.Root);
        await repository.WriteAllBytesAsync(ChunkIndexService.RepairInProgressMarkerPath, [], CancellationToken.None);
        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);

        await Should.ThrowAsync<ChunkIndexRepairIncompleteException>(() => index.FlushAsync());
    }

    [Test]
    public async Task InvalidateCaches_DeletesShardCacheButKeepsRepairMarker()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("invalidate-marker");
        var repository = new RelativeFileSystem(RepositoryLocalStatePaths.GetRepositoryRoot(repositoryKey, repositoryKey));
        var cache = new RelativeFileSystem(RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey));
        repository.CreateDirectory(RelativePath.Root);
        cache.CreateDirectory(RelativePath.Root);
        await repository.WriteAllBytesAsync(ChunkIndexService.RepairInProgressMarkerPath, [], CancellationToken.None);
        await cache.WriteAllBytesAsync(RelativePath.Root / PathSegment.Parse("aa"), [1], CancellationToken.None);
        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);

        index.InvalidateCaches();

        repository.FileExists(ChunkIndexService.RepairInProgressMarkerPath).ShouldBeTrue();
        cache.FileExists(RelativePath.Root / PathSegment.Parse("aa")).ShouldBeFalse();
    }

    private static ChunkIndexService CreateIndex(FakeInMemoryBlobContainerService blobs, string name)
    {
        var repositoryKey = UniqueRepositoryKey(name);
        return new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);
    }

    private static string UniqueRepositoryKey(string name) => $"acct-{name}-{Guid.NewGuid():N}";
}
