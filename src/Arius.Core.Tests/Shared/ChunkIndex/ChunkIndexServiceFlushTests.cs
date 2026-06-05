using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Tests.Shared.ChunkIndex.Fakes;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkIndex;

public class ChunkIndexServiceFlushTests
{
    private static readonly PlaintextPassthroughService s_encryption = new();

    [Test]
    public async Task FlushAsync_GetsDirtyPrefixesFromSqlite_AndUploadsMergedShard()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("flush-merge");
        var snapshot = new FakeSnapshotService([BlobPaths.SnapshotPath(new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero))]);

        var cleanHash = FakeContentHash('a');
        var dirtyHash = ContentHash.Parse($"{cleanHash.Prefix(ChunkIndexService.ShardPrefixLength)}{new string('d', 64 - ChunkIndexService.ShardPrefixLength)}");
        var prefix = Shard.PrefixOf(cleanHash);
        var cleanEntry = new ShardEntry(cleanHash, FakeChunkHash('b'), 10, 5);
        var dirtyEntry = new ShardEntry(dirtyHash, FakeChunkHash('c'), 20, 8);
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(prefix),
            await ShardSerializer.SerializeAsync(CreateShard(cleanEntry), s_encryption),
            BlobTier.Cool);

        using var index = new ChunkIndexService(blobs, s_encryption, snapshot, repositoryKey, repositoryKey);
        index.AddEntry(dirtyEntry);

        await index.FlushAsync();

        snapshot.ListBlobNamesCallCount.ShouldBe(1);
        var flushed = await ReadShardAsync(blobs, prefix);
        flushed.Entries.ShouldContain(entry => entry.ContentHash == cleanHash);
        flushed.Entries.ShouldContain(entry => entry.ContentHash == dirtyHash);
        (await index.LookupAsync(cleanHash)).ShouldBe(cleanEntry);
        (await index.LookupAsync(dirtyHash)).ShouldBe(dirtyEntry);
    }

    private static async Task<Shard> ReadShardAsync(FakeInMemoryBlobContainerService blobs, PathSegment prefix)
    {
        var download = await blobs.DownloadAsync(BlobPaths.ChunkIndexShardPath(prefix), CancellationToken.None);
        await using var stream = download.Stream;
        return ShardSerializer.Deserialize(stream, s_encryption);
    }

    private static Shard CreateShard(params ShardEntry[] entries)
    {
        var shard = new Shard();
        shard.AddOrUpdateRange(entries);
        return shard;
    }

    private static string UniqueRepositoryKey(string name) => $"acct-{name}-{Guid.NewGuid():N}";
}
