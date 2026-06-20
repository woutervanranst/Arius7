using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Compression;
using Arius.Core.Tests.Fakes;
using Arius.Core.Tests.Shared.Snapshot.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkIndex;

/// <summary>
/// Proves the run-scoped chunk-index listing cache (#2), new/empty-repository behaviour (#7), and the parallel
/// shard-download path (#9). The single full <c>chunk-index/</c> listing is recorded by the Fake as
/// <see cref="ExpectedFullListPrefix"/>; per-run reuse means a second uncovered lookup adds zero listings.
/// </summary>
public class ChunkIndexServiceListingCacheTests
{
    private static readonly string ExpectedFullListPrefix = $"{BlobPaths.ChunkIndexPrefix}/";

    // ── #2: one full listing serves every root/leaf for the whole run ────────────────────────────────────────

    [Test]
    public async Task LookupAsync_SecondUncoveredRoot_ReusesCachedListing_NoExtraList()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var hashAa = FakeContentHash('a'); // root "aa"
        var hashBb = FakeContentHash('b'); // root "bb"
        var entryAa = new ShardEntry(hashAa, FakeChunkHash('1'), 10, 5, BlobTier.Cool);
        var entryBb = new ShardEntry(hashBb, FakeChunkHash('2'), 20, 8, BlobTier.Cool);
        await SeedShardAsync(blobs, hashAa, entryAa);
        await SeedShardAsync(blobs, hashBb, entryBb);
        using var index = CreateIndex(blobs, "listing-reuse");

        // First uncovered lookup pays for exactly one full listing.
        (await index.LookupAsync(hashAa)).ShouldBe(entryAa);
        blobs.ListedNamePrefixes.ShouldBe([ExpectedFullListPrefix]);
        blobs.ClearListedNamePrefixes();

        // A second lookup in a never-before-touched root reuses the cached listing: zero additional lists.
        (await index.LookupAsync(hashBb)).ShouldBe(entryBb);
        blobs.ListedNamePrefixes.ShouldBeEmpty();
    }

    // ── #7: a brand-new repository lists once and downloads nothing ──────────────────────────────────────────

    [Test]
    public async Task LookupAsync_EmptyRepository_SingleListing_NoDownloads()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        using var index = CreateIndex(blobs, "empty-repo"); // FakeSnapshotService → "<none>"

        var result = await index.LookupAsync([FakeContentHash('a'), FakeContentHash('b'), FakeContentHash('c')]);

        result.ShouldBeEmpty();
        blobs.ListedNamePrefixes.ShouldBe([ExpectedFullListPrefix]); // exactly one listing across all roots
        blobs.RequestedBlobNames.ShouldBeEmpty();                    // nothing to download
    }

    // ── #7: an orphan shard left by a crash between flush (6b) and snapshot (6d) is still discovered ─────────

    [Test]
    public async Task LookupAsync_OrphanShardWithNoSnapshot_IsDiscovered()
    {
        // Remote has a shard but snapshots/ is empty (latest == "<none>"): the exact crash-between-flush-and-
        // snapshot state. Discovery is by blob existence, so the orphan entry must still be found — a snapshot
        // shortcut would miss it and (at flush, overwrite:true) silently drop it.
        var blobs = new FakeInMemoryBlobContainerService();
        var hash = FakeContentHash('a');
        var entry = new ShardEntry(hash, FakeChunkHash('1'), 10, 5, BlobTier.Cool);
        await SeedShardAsync(blobs, hash, entry);
        using var index = CreateIndex(blobs, "orphan-no-snapshot");

        var actual = await index.LookupAsync(hash);

        actual.ShouldBe(entry);
        blobs.ListedNamePrefixes.ShouldBe([ExpectedFullListPrefix]);
        blobs.RequestedBlobNames.ShouldContain(BlobPaths.ChunkIndexShardPath(ChunkIndexRouter.GetRootPrefix(hash)));
    }

    // ── #9: many sibling leaves under one root load in parallel and all resolve, each downloaded once ────────

    [Test]
    public async Task LookupAsync_ManyLeavesOneRoot_LoadsEachLeafOnce_AndResolvesAll()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var entriesByHash = new Dictionary<ContentHash, ShardEntry>();
        var leafBlobs = new List<RelativePath>();
        foreach (var nibble in "0123456789abcdef")
        {
            var hash = ContentHash.Parse($"aa{nibble}".PadRight(64, '0')); // leaf shard "aa{nibble}"
            var entry = new ShardEntry(hash, FakeChunkHash(nibble), 10, 5, BlobTier.Cool);
            entriesByHash[hash] = entry;
            var leafPrefix = PathSegment.Parse($"aa{nibble}");
            var blobName = BlobPaths.ChunkIndexShardPath(leafPrefix);
            leafBlobs.Add(blobName);
            blobs.SeedBlob(blobName, await ShardSerializer.SerializeAsync(CreateShard(entry), IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance), BlobTier.Cool);
        }
        using var index = CreateIndex(blobs, "many-leaves");

        var actual = await index.LookupAsync(entriesByHash.Keys);

        actual.Count.ShouldBe(entriesByHash.Count);
        foreach (var (hash, entry) in entriesByHash)
            actual[hash].ShouldBe(entry);
        foreach (var leafBlob in leafBlobs)
            blobs.RequestedBlobNames.Count(n => n == leafBlob).ShouldBe(1); // one download per leaf, no duplicates
        blobs.ListedNamePrefixes.ShouldBe([ExpectedFullListPrefix]);        // one listing served all leaves
    }

    // ── #9: sibling-leaf coverage downloads actually run concurrently (not just correctly) ──────────────────

    [Test]
    public async Task LookupAsync_ManyLeavesOneRoot_DownloadsRunConcurrently()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var entriesByHash = new Dictionary<ContentHash, ShardEntry>();
        foreach (var nibble in "01234567") // 8 leaves == PrefixLoadWorkers
        {
            var hash = ContentHash.Parse($"aa{nibble}".PadRight(64, '0'));
            var entry = new ShardEntry(hash, FakeChunkHash(nibble), 10, 5, BlobTier.Cool);
            entriesByHash[hash] = entry;
            blobs.SeedBlob(BlobPaths.ChunkIndexShardPath(PathSegment.Parse($"aa{nibble}")), await ShardSerializer.SerializeAsync(CreateShard(entry), IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance), BlobTier.Cool);
        }
        // The barrier only releases once all 8 downloads are simultaneously in flight; sequential loads would
        // never reach it and the lookup would time out.
        var barrier = new ParallelDownloadBarrierBlobContainerService(blobs, expectedConcurrency: 8);
        using var index = CreateIndex(barrier, "parallel-downloads");

        var actual = await index.LookupAsync(entriesByHash.Keys);

        barrier.ReachedExpectedConcurrency.ShouldBeTrue();
        actual.Count.ShouldBe(entriesByHash.Count);
        foreach (var (hash, entry) in entriesByHash)
            actual[hash].ShouldBe(entry);
    }

    // ── #2 reset: InvalidateCaches drops the run-scoped listing so the next lookup re-lists ──────────────────

    [Test]
    public async Task InvalidateCaches_ForcesFreshListingOnNextLookup()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var hash = FakeContentHash('a');
        var entry = new ShardEntry(hash, FakeChunkHash('1'), 10, 5, BlobTier.Cool);
        await SeedShardAsync(blobs, hash, entry);
        using var index = CreateIndex(blobs, "invalidate-relists");

        (await index.LookupAsync(hash)).ShouldBe(entry);
        index.InvalidateCaches();
        (await index.LookupAsync(hash)).ShouldBe(entry);

        // Two full listings: one before InvalidateCaches, one forced after it.
        blobs.ListedNamePrefixes.ShouldBe([ExpectedFullListPrefix, ExpectedFullListPrefix]);
    }

    // ── #9 race: a listed-but-gone shard triggers exactly one re-list, then resolves as empty ───────────────

    [Test]
    public async Task LookupAsync_ListedShardGoneAtDownload_RelistsOnce_ThenTreatsRangeAsEmpty()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var hash = FakeContentHash('a');
        var entry = new ShardEntry(hash, FakeChunkHash('1'), 10, 5, BlobTier.Cool);
        await SeedShardAsync(blobs, hash, entry); // listed, but the racing container makes its download vanish
        var racing = new RacingDownloadBlobContainerService(blobs, BlobPaths.ChunkIndexShardPath(ChunkIndexRouter.GetRootPrefix(hash)));
        using var index = CreateIndex(racing, "race-gone");

        (await index.LookupAsync(hash)).ShouldBeNull();

        // Exactly one re-list (the single-shot retry latch), then the range is claimed empty.
        blobs.ListedNamePrefixes.ShouldBe([ExpectedFullListPrefix, ExpectedFullListPrefix]);
        blobs.ClearListedNamePrefixes();
        (await index.LookupAsync(hash)).ShouldBeNull();
        blobs.ListedNamePrefixes.ShouldBeEmpty(); // the empty claim makes the repeat a pure local hit
    }

    // ── #9 race convergence: a concurrent split lands; the re-list resolves to the new child shard ───────────

    [Test]
    public async Task LookupAsync_SplitRaceDuringDownload_RelistsOnce_AndResolvesViaChild()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var hash = FakeContentHash('a'); // "aaaa…" → parent "aa", child "aaa"
        var entry = new ShardEntry(hash, FakeChunkHash('1'), 10, 5, BlobTier.Cool);
        var parentBlob = BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aa"));
        var childBlob = BlobPaths.ChunkIndexShardPath(PathSegment.Parse("aaa"));
        await SeedShardAsync(blobs, hash, entry); // parent shard "aa" = { hash }
        var childBytes = await ShardSerializer.SerializeAsync(CreateShard(entry), IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);

        // On the first download of the parent, simulate the racing split: parent deleted, child written.
        var racing = new RacingDownloadBlobContainerService(blobs, parentBlob, onFirstMiss: () =>
        {
            blobs.DeleteAsync(parentBlob).GetAwaiter().GetResult();
            blobs.SeedBlob(childBlob, childBytes, BlobTier.Cool);
        })
        { MaxMisses = 1 };
        using var index = CreateIndex(racing, "race-split");

        var actual = await index.LookupAsync(hash);

        actual.ShouldBe(entry); // resolved through the post-split child shard
        blobs.ListedNamePrefixes.ShouldBe([ExpectedFullListPrefix, ExpectedFullListPrefix]); // exactly one re-list
    }

    // ── Robustness: a transient listing fault must not poison the run — the next lookup re-lists ─────────────

    [Test]
    public async Task LookupAsync_TransientListFailure_RecoversOnNextLookup()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var hash = FakeContentHash('a');
        var entry = new ShardEntry(hash, FakeChunkHash('1'), 10, 5, BlobTier.Cool);
        await SeedShardAsync(blobs, hash, entry);
        var faulting = new FaultOnceListBlobContainerService(blobs);
        using var index = CreateIndex(faulting, "transient-list-fault");

        // First lookup: the listing faults and the call throws — but the faulted listing must not be pinned.
        await Should.ThrowAsync<InvalidOperationException>(() => index.LookupAsync(hash));

        // Next lookup re-lists fresh and recovers (the pre-cache per-call recovery property).
        (await index.LookupAsync(hash)).ShouldBe(entry);
    }

    // ── CHANGE C: the split-threshold design point is deliberate (see docs/design/core/shared/chunk-index.md) ────────────────────────

    [Test]
    public void MaxShardEntryCount_DesignPoint_IsPinnedTo1024()
    {
        // 1024 keeps incremental flush re-uploads cheap (~11 KB/touched shard) and keeps ~1.3 M chunks within a
        // single 5000-blob list page (~4096 leaf shards). Changing it is a deliberate trade-off — update the
        // rationale in docs/design/core/shared/chunk-index.md when you flip this.
        ChunkIndexService.MaxShardEntryCount.ShouldBe(1024);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task SeedShardAsync(FakeInMemoryBlobContainerService blobs, ContentHash hash, params ShardEntry[] entries)
        => blobs.SeedBlob(
            BlobPaths.ChunkIndexShardPath(ChunkIndexRouter.GetRootPrefix(hash)),
            await ShardSerializer.SerializeAsync(CreateShard(entries), IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance),
            BlobTier.Cool);

    private static ChunkIndexService CreateIndex(IBlobContainerService blobs, string name)
    {
        var repositoryKey = $"acct-{name}-{Guid.NewGuid():N}";
        return new ChunkIndexService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance, new FakeSnapshotService(), repositoryKey, repositoryKey);
    }

    private static Shard CreateShard(params ShardEntry[] entries)
    {
        var shard = new Shard();
        shard.AddOrUpdateRange(entries);
        return shard;
    }
}
