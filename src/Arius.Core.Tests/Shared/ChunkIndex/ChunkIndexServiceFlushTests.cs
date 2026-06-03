using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
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
        var snapshotBlob = SnapshotService.BlobName(new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero));
        blobs.SeedBlob(snapshotBlob, [1], BlobTier.Cool);

        var cleanHash = FakeContentHash('a');
        var dirtyHash = ContentHash.Parse($"{cleanHash.Prefix(ChunkIndexService.ShardPrefixLength)}{new string('d', 64 - ChunkIndexService.ShardPrefixLength)}");
        var prefix = Shard.PrefixOf(cleanHash);
        var cleanEntry = new ShardEntry(cleanHash, FakeChunkHash('b'), 10, 5);
        var dirtyEntry = new ShardEntry(dirtyHash, FakeChunkHash('c'), 20, 8);
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(prefix),
            await ShardSerializer.SerializeAsync(CreateShard(cleanEntry), s_encryption),
            BlobTier.Cool);

        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);
        index.AddEntry(dirtyEntry);

        await index.FlushAsync();

        var flushed = await ReadShardAsync(blobs, prefix);
        flushed.Entries.ShouldContain(entry => entry.ContentHash == cleanHash);
        flushed.Entries.ShouldContain(entry => entry.ContentHash == dirtyHash);
        (await index.LookupAsync(cleanHash)).ShouldBe(cleanEntry);
        (await index.LookupAsync(dirtyHash)).ShouldBe(dirtyEntry);
    }

    [Test]
    public async Task FlushAsync_RefreshesStaleCleanRowsButPreservesDirtyRows()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("flush-refresh");
        var firstSnapshot = SnapshotService.BlobName(new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero));
        var secondSnapshot = SnapshotService.BlobName(new DateTimeOffset(2026, 3, 23, 15, 0, 0, TimeSpan.Zero));
        blobs.SeedBlob(firstSnapshot, [1], BlobTier.Cool);

        var cleanHash = FakeContentHash('a');
        var dirtyHash = ContentHash.Parse($"{cleanHash.Prefix(ChunkIndexService.ShardPrefixLength)}{new string('d', 64 - ChunkIndexService.ShardPrefixLength)}");
        var prefix = Shard.PrefixOf(cleanHash);
        var originalClean = new ShardEntry(cleanHash, FakeChunkHash('b'), 10, 5);
        var refreshedClean = new ShardEntry(cleanHash, FakeChunkHash('c'), 20, 8);
        var dirtyEntry = new ShardEntry(dirtyHash, FakeChunkHash('e'), 30, 12);
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(prefix),
            await ShardSerializer.SerializeAsync(CreateShard(originalClean), s_encryption),
            BlobTier.Cool);

        using var index = new ChunkIndexService(blobs, s_encryption, repositoryKey, repositoryKey);
        (await index.LookupAsync(cleanHash)).ShouldBe(originalClean);
        index.AddEntry(dirtyEntry);

        blobs.SeedBlob(secondSnapshot, [2], BlobTier.Cool);
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(prefix),
            await ShardSerializer.SerializeAsync(CreateShard(refreshedClean), s_encryption),
            BlobTier.Cool);

        await index.FlushAsync();

        var flushed = await ReadShardAsync(blobs, prefix);
        flushed.Entries.Single(entry => entry.ContentHash == cleanHash).ShouldBe(refreshedClean);
        flushed.Entries.Single(entry => entry.ContentHash == dirtyHash).ShouldBe(dirtyEntry);
    }

    private static async Task<Shard> ReadShardAsync(FakeInMemoryBlobContainerService blobs, PathSegment prefix)
    {
        await using var stream = await blobs.DownloadAsync(BlobPaths.ChunkIndexShardPath(prefix), CancellationToken.None);
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
