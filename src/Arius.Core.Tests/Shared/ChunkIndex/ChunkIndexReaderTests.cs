using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ChunkIndexReaderTests
{
    private static readonly PlaintextPassthroughService s_encryption = new();

    [Test]
    public async Task LookupAsync_BatchesPersistedMissesByShardPrefix()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("reader-batch");
        var firstHash = FakeContentHash('a');
        var secondHash = ContentHash.Parse($"{firstHash.Prefix(ChunkIndexService.ShardPrefixLength)}{new string('b', 64 - ChunkIndexService.ShardPrefixLength)}");
        var missingHash = ContentHash.Parse($"{firstHash.Prefix(ChunkIndexService.ShardPrefixLength)}{new string('c', 64 - ChunkIndexService.ShardPrefixLength)}");
        var otherPrefixHash = FakeContentHash('d');
        var firstEntry = new ShardEntry(firstHash, FakeChunkHash('1'), 10, 5);
        var secondEntry = new ShardEntry(secondHash, FakeChunkHash('2'), 20, 8);
        var otherPrefixEntry = new ShardEntry(otherPrefixHash, FakeChunkHash('3'), 30, 12);
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(firstHash)),
            await ShardSerializer.SerializeAsync(CreateShard(firstEntry, secondEntry), s_encryption),
            BlobTier.Cool);
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(otherPrefixHash)),
            await ShardSerializer.SerializeAsync(CreateShard(otherPrefixEntry), s_encryption),
            BlobTier.Cool);
        var reader = CreateReader(blobs, repositoryKey);

        var actual = await reader.LookupAsync([firstHash, secondHash, missingHash, otherPrefixHash]);

        actual.ShouldBe(new Dictionary<ContentHash, ShardEntry>
        {
            [firstHash] = firstEntry,
            [secondHash] = secondEntry,
            [otherPrefixHash] = otherPrefixEntry,
        });
        actual.ShouldNotContainKey(missingHash);
        blobs.RequestedBlobNames.Count(name => name == BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(firstHash))).ShouldBe(1);
        blobs.RequestedBlobNames.Count(name => name == BlobPaths.ChunkIndexShardPath(Shard.PrefixOf(otherPrefixHash))).ShouldBe(1);
    }

    private static ChunkIndexReader CreateReader(FakeInMemoryBlobContainerService blobs, string repositoryKey)
    {
        var l2 = new RelativeFileSystem(RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey));
        l2.CreateDirectory(RelativePath.Root);
        var cache = new ChunkIndexShardCache(blobs, s_encryption, l2, ChunkIndexService.DefaultL1CacheBudgetBytes);
        return new ChunkIndexReader(cache);
    }

    private static Shard CreateShard(params ShardEntry[] entries)
    {
        var shard = new Shard();
        shard.AddOrUpdateRange(entries);
        return shard;
    }

    private static string UniqueRepositoryKey(string name) => $"acct-{name}-{Guid.NewGuid():N}";
}
