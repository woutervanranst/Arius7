using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ChunkIndexShardCacheTests
{
    private static readonly PlaintextPassthroughService s_encryption = new();

    [Test]
    public async Task UpdateShardAsync_MutatesOwnedShardAndSynchronizesCacheAndStore()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("cache-update");
        var cache = CreateCache(blobs, repositoryKey);
        var contentHash = FakeContentHash('a');
        var prefix = Shard.PrefixOf(contentHash);
        var original = new ShardEntry(contentHash, FakeChunkHash('1'), 10, 5);
        var replacement = new ShardEntry(contentHash, FakeChunkHash('2'), 11, 6);

        await cache.UpdateShardAsync(prefix, [original]);
        await cache.UpdateShardAsync(prefix, [replacement]);

        var actual = await cache.LookupAsync(contentHash);
        actual.ShouldBe(replacement);
        var l2 = new RelativeFileSystem(RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey));
        var localShard = ShardSerializer.DeserializeLocal(await l2.ReadAllBytesAsync(RelativePath.Root / prefix, CancellationToken.None));
        localShard.TryLookup(contentHash, out var localEntry).ShouldBeTrue();
        localEntry.ShouldBe(replacement);
    }

    [Test]
    public async Task GetShardAsync_ReturnsLoadedShard()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var cache = CreateCache(blobs, UniqueRepositoryKey("cache-get-shard"));
        var contentHash = FakeContentHash('a');
        var prefix = Shard.PrefixOf(contentHash);
        var entry = new ShardEntry(contentHash, FakeChunkHash('1'), 10, 5);
        await cache.UpdateShardAsync(prefix, [entry]);

        var shard = await cache.GetShardAsync(prefix);

        shard.TryLookup(contentHash, out var actual).ShouldBeTrue();
        actual.ShouldBe(entry);
    }

    private static ChunkIndexShardCache CreateCache(FakeInMemoryBlobContainerService blobs, string repositoryKey)
    {
        var l2 = new RelativeFileSystem(RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey));
        l2.CreateDirectory(RelativePath.Root);
        return new ChunkIndexShardCache(blobs, s_encryption, l2, ChunkIndexService.DefaultL1CacheBudgetBytes);
    }

    private static string UniqueRepositoryKey(string name) => $"acct-{name}-{Guid.NewGuid():N}";
}
