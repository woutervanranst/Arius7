using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Tests.Shared.Snapshot.Fakes;
using Arius.Tests.Shared.Compression;
using Arius.Tests.Shared.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Arius.Core.Shared.Storage;

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
        var dirtyHash = ContentHash.Parse($"{cleanHash.Prefix(ChunkIndexService.MinShardPrefixLength)}{new string('d', 64 - ChunkIndexService.MinShardPrefixLength)}");
        var prefix = ChunkIndexRouter.GetRootPrefix(cleanHash);
        var cleanEntry = new ShardEntry(cleanHash, FakeChunkHash('b'), 10, 5, BlobTier.Cool);
        var dirtyEntry = new ShardEntry(dirtyHash, FakeChunkHash('c'), 20, 8, BlobTier.Cool);
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(prefix),
            await ShardSerializer.SerializeAsync(CreateShard(cleanEntry), s_encryption, TestCompression.Instance),
            BlobTier.Cool);

        using var index = new ChunkIndexService(blobs, s_encryption, TestCompression.Instance, snapshot, repositoryKey, repositoryKey);
        index.AddEntry(dirtyEntry);

        await index.FlushAsync();

        snapshot.ListBlobNamesCallCount.ShouldBe(1);
        var flushed = await ReadShardAsync(blobs, prefix);
        flushed.Entries.ShouldContain(entry => entry.ContentHash == cleanHash);
        flushed.Entries.ShouldContain(entry => entry.ContentHash == dirtyHash);

        using var resumedIndex = new ChunkIndexService(blobs, s_encryption, TestCompression.Instance, snapshot, repositoryKey, repositoryKey);
        (await resumedIndex.LookupAsync(cleanHash)).ShouldBe(cleanEntry);
        (await resumedIndex.LookupAsync(dirtyHash)).ShouldBe(dirtyEntry);
    }

    [Test]
    public async Task AddEntry_AfterFlushAsyncCompleted_Throws()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("flush-closed");
        using var index = new ChunkIndexService(blobs, s_encryption, TestCompression.Instance, new FakeSnapshotService(), repositoryKey, repositoryKey);
        index.AddEntry(new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool));

        await index.FlushAsync();

        var ex = Should.Throw<InvalidOperationException>(() => index.AddEntry(new ShardEntry(FakeContentHash('c'), FakeChunkHash('d'), 11, 6, BlobTier.Cool)));
        ex.Message.ShouldBe("Chunk-index service cannot be used after flush has started.");
    }

    [Test]
    public async Task FlushAsync_WhenCalledTwice_Throws()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("flush-once");
        using var index = new ChunkIndexService(blobs, s_encryption, TestCompression.Instance, new FakeSnapshotService(), repositoryKey, repositoryKey);

        await index.FlushAsync();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => index.FlushAsync());
        ex.Message.ShouldBe("Chunk-index service cannot be used after flush has started.");
    }

    [Test]
    public async Task LookupAsync_AfterFlush_Throws()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("flush-lookup-closed");
        using var index = new ChunkIndexService(blobs, s_encryption, TestCompression.Instance, new FakeSnapshotService(), repositoryKey, repositoryKey);
        index.AddEntry(new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool));

        await index.FlushAsync();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => index.LookupAsync(FakeContentHash('a')));
        ex.Message.ShouldBe("Chunk-index service cannot be used after flush has started.");
    }

    [Test]
    public async Task FlushAsync_WithPendingEntries_LogsFlushSummary()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("flush-logs");

        var collector = new FakeLogCollector();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new FakeLoggerProvider(collector)));

        using var index = new ChunkIndexService(blobs, s_encryption, TestCompression.Instance, new FakeSnapshotService(), repositoryKey, repositoryKey, loggerFactory);
        index.AddEntry(new ShardEntry(FakeContentHash('a'), FakeChunkHash('b'), 10, 5, BlobTier.Cool));

        await index.FlushAsync();

        var messages = collector.GetSnapshot().Select(record => record.Message).ToArray();
        messages.ShouldContain(message => message.Contains("Flushing", StringComparison.Ordinal));
        messages.ShouldContain(message => message.Contains("Flushed", StringComparison.Ordinal));
    }

    private static async Task<Shard> ReadShardAsync(FakeInMemoryBlobContainerService blobs, PathSegment prefix)
    {
        var download = await blobs.DownloadAsync(BlobPaths.ChunkIndexShardPath(prefix), CancellationToken.None);
        await using var stream = download.Stream;
        return ShardSerializer.Deserialize(stream, s_encryption, TestCompression.Instance);
    }

    private static Shard CreateShard(params ShardEntry[] entries)
    {
        var shard = new Shard();
        shard.AddOrUpdateRange(entries);
        return shard;
    }

    private static string UniqueRepositoryKey(string name) => $"acct-{name}-{Guid.NewGuid():N}";
}
