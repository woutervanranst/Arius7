using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Tests.Fakes;
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

    // ── Split at threshold ───────────────────────────────────────────────────

    [Test]
    public async Task FlushAsync_ShardExceedsThreshold_SplitsIntoChildrenAndDeletesParent()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("flush-split");
        var entries = new[] { Entry("aa1"), Entry("aa2"), Entry("aa3"), Entry("aa4"), Entry("aa5") };
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa")),
            await ShardSerializer.SerializeAsync(CreateShard(entries[0], entries[1], entries[2]), s_encryption),
            BlobTier.Cool);

        using (var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey, maxShardEntryCount: 3))
        {
            index.AddEntry(entries[3]);
            index.AddEntry(entries[4]);
            await index.FlushAsync();
        }

        // The parent is deleted only AFTER all children were written; only non-empty children exist.
        blobs.DeletedBlobNames.ShouldContain(BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa")));
        (await blobs.TryDownloadAsync(BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa")))).ShouldBeNull();
        foreach (var entry in entries)
        {
            var leafPrefix = PathSegment.Parse(entry.ContentHash.Prefix(3));
            (await ReadShardAsync(blobs, leafPrefix)).Entries.Single().ShouldBe(entry);
        }

        // A cold machine resolves every hash through the split layout.
        using var cold = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), UniqueRepositoryKey("flush-split-cold"), UniqueRepositoryKey("flush-split-cold"));
        foreach (var entry in entries)
            (await cold.LookupAsync(entry.ContentHash)).ShouldBe(entry);
    }

    [Test]
    public async Task FlushAsync_ChildStillOverThreshold_SplitsRecursively()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("flush-split-recursive");
        var first = Entry("aa30");
        var second = Entry("aa3f");

        using (var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey, maxShardEntryCount: 1))
        {
            index.AddEntry(first);
            index.AddEntry(second);
            await index.FlushAsync();
        }

        // Both entries share "aa3", which still exceeds the threshold → split one level deeper.
        // Intermediate prefixes ("aa", "aa3") are never written.
        (await ReadShardAsync(blobs, PathSegment.Parse("aa30"))).Entries.Single().ShouldBe(first);
        (await ReadShardAsync(blobs, PathSegment.Parse("aa3f"))).Entries.Single().ShouldBe(second);
        (await blobs.TryDownloadAsync(BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa")))).ShouldBeNull();
        (await blobs.TryDownloadAsync(BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa3")))).ShouldBeNull();
    }

    [Test]
    public async Task FlushAsync_InterruptedSplit_ParentWinsForReaders_AndRetryConverges()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("flush-split-crash");
        var e1 = Entry("aa1");
        var e2 = Entry("aa2");
        var e3 = Entry("aa3");
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa")),
            await ShardSerializer.SerializeAsync(CreateShard(e1, e2), s_encryption),
            BlobTier.Cool);

        // The split dies after the first child upload: parent + one child coexist.
        var faulting = new FaultingChunkIndexUploadBlobContainerService(blobs) { AllowedChunkIndexUploads = 1 };
        using (var crashed = new ChunkIndexService(faulting, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey, maxShardEntryCount: 2))
        {
            crashed.AddEntry(e3);
            await Should.ThrowAsync<InvalidOperationException>(() => crashed.FlushAsync());
        }

        (await blobs.TryDownloadAsync(BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa")))).ShouldNotBeNull(); // parent intact
        (await ReadShardAsync(blobs, PathSegment.Parse("aa1"))).Entries.Single().ShouldBe(e1);                    // one child written

        // PARENT WINS: a cold reader resolves everything any published snapshot could reference from
        // the parent, and does not see the crashed run's unpublished entry.
        using (var coldReader = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), UniqueRepositoryKey("flush-split-crash-reader"), UniqueRepositoryKey("flush-split-crash-reader")))
        {
            (await coldReader.LookupAsync(e1.ContentHash)).ShouldBe(e1);
            (await coldReader.LookupAsync(e2.ContentHash)).ShouldBe(e2);
            (await coldReader.LookupAsync(e3.ContentHash)).ShouldBeNull(); // unpublished → invisible
        }

        // Retry on the same machine: the pending row survived, the split re-runs and converges.
        using (var retry = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey, maxShardEntryCount: 2))
            await retry.FlushAsync();

        (await blobs.TryDownloadAsync(BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa")))).ShouldBeNull(); // parent gone
        (await ReadShardAsync(blobs, PathSegment.Parse("aa1"))).Entries.Single().ShouldBe(e1);
        (await ReadShardAsync(blobs, PathSegment.Parse("aa2"))).Entries.Single().ShouldBe(e2);
        (await ReadShardAsync(blobs, PathSegment.Parse("aa3"))).Entries.Single().ShouldBe(e3);

        using var cold = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), UniqueRepositoryKey("flush-split-crash-cold"), UniqueRepositoryKey("flush-split-crash-cold"));
        (await cold.LookupAsync(e3.ContentHash)).ShouldBe(e3);
    }

    [Test]
    public async Task FlushAsync_NewEntriesInEmptyChildRangeOfSplitRoot_WritesOnlyThatChild()
    {
        // A completed split left only non-empty children; a later run adds entries in a range that
        // had no blob. Flush writes exactly that new child — it never resurrects the parent.
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("flush-empty-child");
        var existing = Entry("aa1");
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa1")),
            await ShardSerializer.SerializeAsync(CreateShard(existing), s_encryption),
            BlobTier.Cool);

        var newEntry = Entry("aa7");
        using (var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey))
        {
            index.AddEntry(newEntry);
            await index.FlushAsync();
        }

        blobs.UploadedBlobNames.ShouldBe([BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa7"))]);
        blobs.DeletedBlobNames.ShouldBeEmpty();
        (await blobs.TryDownloadAsync(BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa")))).ShouldBeNull();
        (await ReadShardAsync(blobs, PathSegment.Parse("aa7"))).Entries.Single().ShouldBe(newEntry);
    }

    [Test]
    public async Task FlushAsync_Split_DeletesForeignStaleChildInRange()
    {
        // A foreign machine's crashed split left a stale child next to the authoritative parent.
        // Our split rewrites the layout and deletes every blob in the parent's range we did not
        // just write — including that stale child (its entries were never published).
        var blobs = new FakeInMemoryBlobContainerService();
        var repositoryKey = UniqueRepositoryKey("flush-stale-child");
        var e1 = Entry("aa1");
        var e2 = Entry("aa2");
        var e3 = Entry("aa3");
        var foreignUnpublished = Entry("aa9");
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa")),
            await ShardSerializer.SerializeAsync(CreateShard(e1, e2), s_encryption),
            BlobTier.Cool);
        blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa9")),
            await ShardSerializer.SerializeAsync(CreateShard(foreignUnpublished), s_encryption),
            BlobTier.Cool);

        using (var index = new ChunkIndexService(blobs, s_encryption, new FakeSnapshotService(), repositoryKey, repositoryKey, maxShardEntryCount: 2))
        {
            index.AddEntry(e3);
            await index.FlushAsync();
        }

        blobs.DeletedBlobNames.ShouldContain(BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa")));
        blobs.DeletedBlobNames.ShouldContain(BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa9")));
        (await ReadShardAsync(blobs, PathSegment.Parse("aa3"))).Entries.Single().ShouldBe(e3);
    }

    /// <summary>An entry whose content hash starts with <paramref name="hashPrefix"/> (padded with '9').</summary>
    private static ShardEntry Entry(string hashPrefix)
        => new(ContentHash.Parse(hashPrefix.PadRight(64, '9')), FakeChunkHash('e'), 10, 5, BlobTier.Cool);

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
