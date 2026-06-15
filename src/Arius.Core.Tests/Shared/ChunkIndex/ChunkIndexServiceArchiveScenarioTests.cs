using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Tests.Fakes;
using Arius.Core.Tests.Shared.Snapshot.Fakes;
using Arius.Tests.Shared.Storage;
using Microsoft.Data.Sqlite;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkIndex;

/// <summary>
/// Scenario tests that replay how successive <c>archive</c> runs drive <see cref="ChunkIndexService"/> and walk every
/// path of <c>EnsurePrefixLoadedAndSynchronizedAsync</c>, asserting the resulting blob-call pattern and cache-sync state.
/// <para>
/// Each simulated "run" is a fresh <see cref="ChunkIndexService"/> instance replaying the handler's call sequence
/// (LookupAsync per content hash → AddEntry after upload → FlushAsync → PromoteToSnapshotVersionAsync). "Same machine"
/// reuses one (account, container) cache identity; "another machine" uses a different identity over the same shared
/// blob store (shard blob paths are container-relative, so the remote is shared while local caches are independent).
/// </para>
/// The four sync paths: <b>P1</b> cache-at-latest-snapshot (0 calls) · <b>P2</b> remote-missing (1 TryDownload→null) ·
/// <b>P3a</b> ETag-match (1 TryDownload, no re-ingest) · <b>P3b</b> ETag-differs (1 TryDownload + deserialize).
/// </summary>
public class ChunkIndexServiceArchiveScenarioTests
{
    /*
       EnsurePrefixLoadedAndSynchronizedAsync(prefix, latestSnapshot):

       Is the local cache already validated against latestSnapshot?
         │
         ├─ yes ───────────────────────────────────────►  P1   return immediately        — 0 remote calls
         │
         └─ no → TryDownloadAsync(chunk-index/<prefix>)
                   │
                   ├─ null  (no shard on the remote) ────►  P2   AddEmptyPrefix            — 1 download (a miss)
                   │
                   └─ shard exists → does its ETag match the one we cached?
                               │
                               ├─ yes ───────────────────►  P3a  bump snapshot stamp only  — 1 download, NO re-parse
                               │
                               └─ no  ────────────────────►  P3b  deserialize + replace     — 1 download + parse
     
    
    How each scenario now reads at a glance:

       ┌──────────────────────┬───────────────────────────────┬─────────────────────────┬──────────────────────────────────┐
       │       Scenario       │     Arrange (start state)     │       Act (paths)       │         Assert (proves)          │
       ├──────────────────────┼───────────────────────────────┼─────────────────────────┼──────────────────────────────────┤
       │ 1 new repo           │ empty remote + empty cache,   │ P2, P1, P2 → flush →    │ 2 downloads, 2 uploads, then     │
       │                      │ snapshot <none>               │ promote                 │ re-probe 0 downloads = in sync   │
       ├──────────────────────┼───────────────────────────────┼─────────────────────────┼──────────────────────────────────┤
       │ 2 2nd run same       │ warm remote + warm cache @    │ all P1, h4 new → flush  │ 0 downloads, 1 upload (merged    │
       │ machine              │ s1; stage h4 in aa            │ aa only                 │ shard)                           │
       ├──────────────────────┼───────────────────────────────┼─────────────────────────┼──────────────────────────────────┤
       │ 3a another machine,  │ remote @ s1, machine B cache  │ P3b, P1, P3b            │ 1 download per prefix, 0 uploads │
       │ empty cache          │ empty                         │                         │                                  │
       ├───────────────────────┼────────────────────────────────┼──────────────────────────┼───────────────────────────────────┤
       │ 3b another machine,   │ B cached aa@A1/s1, then A      │ snapshot moved + ETag    │ 1 download, stale prefix          │
       │ stale ETag            │ rewrote it → A2/s2             │ differs → P3b            │ refreshed (h5 found)              │
       ├───────────────────────┼────────────────────────────────┼──────────────────────────┼───────────────────────────────────┤
       │ 4 cheap revalidation  │ B cached aa@A1/s1, then A      │ snapshot moved + ETag    │ 1 cheap probe, aa cached at s2    │
       │                       │ touched a different prefix     │ matches → P3a            │ then 0 further reads              │
       ├───────────────────────┼────────────────────────────────┼──────────────────────────┼───────────────────────────────────┤
       │ 5 flush failure       │ crashed run left dirty rows,   │ retry on a fresh         │ dirty rows survived, merged shard │
       │                       │ nothing on remote              │ non-faulting instance    │  uploaded                         │
       ├───────────────────────┼────────────────────────────────┼──────────────────────────┼───────────────────────────────────┤
       │ 6 no-op               │ fully warm @ s1, nothing       │ all P1, flush 0-dirty    │ 0 downloads, 0 uploads            │
       │                       │ changed                        │ early-return             │                                   │
       └───────────────────────┴────────────────────────────────┴──────────────────────────┴───────────────────────────────────┘
     */

    private static readonly PlaintextPassthroughService s_encryption = new();

    // ── Scenario 1: brand-new repository, first run (P2 → flush → promote → P1) ──────────────────────────────

    [Test]
    public async Task Scenario1_NewRepositoryFirstRun_DownloadsEmptyPrefixes_ThenUploadsAndSyncsToLatestSnapshot()
    {
        // Arrange — a brand-new repository: nothing local, nothing remote, no snapshots yet.
        //   Three brand-new chunks, bucketed by their 2-char shard prefix:
        //
        //       h1 = "aaaa...aa"  ┐
        //       h2 = "aabb...bb"  ┘ prefix "aa"
        //       h3 = "bbbb...bb"    prefix "bb"
        //
        //   Starting state:   REMOTE = (empty)    LOCAL CACHE = (empty)    latest snapshot = "<none>"
        var blobs   = new FakeInMemoryBlobContainerService();
        var account = UniqueRepositoryKey("s1-new-repo");

        var h1 = FakeContentHash('a');
        var h2 = SamePrefix(h1, 'b');
        var h3 = FakeContentHash('b');
        var e1 = new ShardEntry(h1, FakeChunkHash('1'), 10, 5, BlobTier.Cool);
        var e2 = new ShardEntry(h2, FakeChunkHash('2'), 20, 8, BlobTier.Cool);
        var e3 = new ShardEntry(h3, FakeChunkHash('3'), 30, 12, BlobTier.Cool);
        var prefixAa = Shard.PrefixOf(h1);
        var prefixBb = Shard.PrefixOf(h3);

        // Act — one complete first archive run: look up each hash, record the new entries, flush, then promote.
        //       Lookup(h1) → P2 (TryDownload "aa" → miss) → AddEmptyPrefix("aa")
        //       Lookup(h2) → P1 ("aa" already empty for "<none>" → no remote call)
        //       Lookup(h3) → P2 (TryDownload "bb" → miss) → AddEmptyPrefix("bb")
        //       FlushAsync → one upload per dirty prefix; PromoteToSnapshotVersionAsync → "<none>" becomes "s1".
        using (var run1 = NewRun(blobs, account, new FakeSnapshotService()))
        {
            (await run1.LookupAsync(h1)).ShouldBeNull(); // P2: remote miss → AddEmptyPrefix("aa")
            (await run1.LookupAsync(h2)).ShouldBeNull(); // P1: "aa" already empty for this snapshot
            (await run1.LookupAsync(h3)).ShouldBeNull(); // P2: remote miss → AddEmptyPrefix("bb")

            run1.AddEntry(e1);
            run1.AddEntry(e2);
            run1.AddEntry(e3);

            await run1.FlushAsync();
            await run1.PromoteToSnapshotVersionAsync("s1");
        }

        // Assert — exactly the remote traffic we expect, plus proof the cache is now in sync at "s1".
        //   2 downloads = one TryDownload per touched prefix (the 2nd "aa" lookup was a cache hit).
        //   2 uploads   = one shard per touched prefix, each carrying its entries.
        ChunkIndexDownloads(blobs).ShouldBe(2); // one TryDownload per touched prefix; 2nd "aa" lookup is a cache hit
        ChunkIndexUploads(blobs).ShouldBe(2);   // one shard written per touched prefix

        (await ReadRemoteShardAsync(blobs, prefixAa)).Entries.Select(e => e.ContentHash).ShouldBe([h1, h2], ignoreOrder: true);
        (await ReadRemoteShardAsync(blobs, prefixBb)).Entries.Select(e => e.ContentHash).ShouldBe([h3], ignoreOrder: true);

        // Re-probe: a fresh run at "s1" repeats every lookup with zero downloads — all P1 hits, so the cache is in sync.
        blobs.RequestedBlobNames.Clear();
        using (var run2 = NewRun(blobs, account, new FakeSnapshotService([Snapshot("s1")])))
        {
            (await run2.LookupAsync(h1)).ShouldBe(e1);
            (await run2.LookupAsync(h2)).ShouldBe(e2);
            (await run2.LookupAsync(h3)).ShouldBe(e3);
        }
        ChunkIndexDownloads(blobs).ShouldBe(0); // all P1 cache hits

        // White-box: the persisted prefixes are recorded at snapshot "s1" and the uploaded remote ETag.
        ClearPool(account);
        var store = new ChunkIndexLocalStore(RepositoryLocalStatePaths.GetChunkIndexCacheRoot(account, account));
        store.IsPrefixAtSnapshotVersion(prefixAa, "s1").ShouldBeTrue();
        store.IsPrefixAtSnapshotVersion(prefixBb, "s1").ShouldBeTrue();
        var aaEtag = (await blobs.GetMetadataAsync(BlobPaths.ChunkIndexShardPath(prefixAa))).ETag!;
        store.IsPrefixAtETag(prefixAa, aaEtag).ShouldBeTrue();
    }

    // ── Scenario 2: second run, same machine — warm cache, only the changed shard is rewritten ────────────────

    [Test]
    public async Task Scenario2_SecondRunSameMachine_LooksUpWithoutDownload_AndUploadsOnlyTheChangedShard()
    {
        // Arrange — recreate the state a completed FIRST archive run leaves behind, then stage one new chunk.
        //
        //   WarmFirstRunAsync archives three chunks, bucketed by their 2-char shard prefix, then flushes and
        //   promotes to snapshot "s1". Two chunks fall in prefix "aa", one in prefix "bb":
        //
        //       h1 = "aaaa...aa"  ┐
        //       h2 = "aabb...bb"  ┘ prefix "aa"
        //       h3 = "bbbb...bb"    prefix "bb"
        //
        //   It leaves two stores populated (and hands the hashes/entries back via `first`):
        //
        //     REMOTE  (blobs - one shared container)
        //         chunk-index/aa  =  shard{ h1, h2 }      (remote ETag A1)
        //         chunk-index/bb  =  shard{ h3 }          (remote ETag B1)
        //
        //     LOCAL CACHE  (cache.sqlite, keyed by `account`)
        //         clean entries    :  h1, h2 -> prefix aa ,  h3 -> prefix bb
        //         loaded prefixes  :  aa @ snapshot s1, etag A1
        //                             bb @ snapshot s1, etag B1
        //         latest snapshot the run promoted to = "s1"
        //
        //   Then stage ONE new chunk in the ALREADY-CACHED prefix "aa" (it is added in Act, not here):
        //
        //       h4 = "aacc...cc"    prefix "aa"
        //
        //   Finally clear both blob call trackers so the second run's TryDownload/Upload counts start from
        //   zero (they otherwise still hold the warm-up run's 2 downloads + 2 uploads).
        var blobs   = new FakeInMemoryBlobContainerService();
        var account = UniqueRepositoryKey("s2-second-run");
        var first   = await WarmFirstRunAsync(blobs, account);

        var h4 = SamePrefix(first.H1, 'c'); // new content in the EXISTING prefix "aa"
        var e4 = new ShardEntry(h4, FakeChunkHash('4'), 40, 16, BlobTier.Cool);

        blobs.RequestedBlobNames.Clear();
        blobs.UploadedBlobNames.Clear(); // ignore the warm-up run's uploads

        // Act — the second run on the SAME machine (same `account` → same warm cache), now at snapshot "s1":
        //       Lookup(h1) → P1 (warm "aa" @ s1)  → dedup hit
        //       Lookup(h3) → P1 (warm "bb" @ s1)  → dedup hit
        //       Lookup(h4) → P1 ("aa" cached)     → miss, so h4 is new → AddEntry(h4)
        //       FlushAsync → only "aa" is dirty → rewrite that one shard, merging cached {h1,h2}+h4 (no download).
        using (var run = NewRun(blobs, account, new FakeSnapshotService([Snapshot("s1")])))
        {
            (await run.LookupAsync(first.H1)).ShouldBe(first.E1); // dedup hit (P1)
            (await run.LookupAsync(first.H3)).ShouldBe(first.E3); // dedup hit (P1), prefix "bb"
            (await run.LookupAsync(h4)).ShouldBeNull();           // new (P1: "aa" already cached)
            run.AddEntry(e4);
            await run.FlushAsync();                               // snapshot unchanged → no promote this run
        }

        // Assert — a warm cache means no reads, and only the changed shard is written.
        //   0 downloads = every lookup was a P1 hit;  1 upload = only "aa" rewritten ("bb" untouched).
        //   The rewritten "aa" shard = { h1, h2, h4 }: the merge preserved the clean rows without a download.
        ChunkIndexDownloads(blobs).ShouldBe(0); // warm cache at latest snapshot → no remote reads
        ChunkIndexUploads(blobs).ShouldBe(1);   // only the "aa" shard re-written; "bb" left untouched

        // The re-uploaded "aa" shard merged the existing clean rows with the new entry — without any download.
        (await ReadRemoteShardAsync(blobs, first.PrefixAa)).Entries.Select(e => e.ContentHash)
            .ShouldBe([first.H1, first.H2, h4], ignoreOrder: true);
    }

    // ── Scenario 3a: another machine, empty local cache → download each touched prefix once (P3b) ──────────────

    [Test]
    public async Task Scenario3a_AnotherMachineEmptyCache_DownloadsEachTouchedPrefixOnce()
    {
        // Arrange — machine A has already archived (WarmFirstRunAsync): the shared REMOTE holds
        //   chunk-index/aa = {h1,h2} and chunk-index/bb = {h3} at snapshot "s1".
        //   Machine B is a DIFFERENT cache identity over the SAME remote, so its local cache starts EMPTY:
        //
        //     REMOTE (shared)               MACHINE B LOCAL CACHE
        //       chunk-index/aa = {h1,h2}      (empty)
        //       chunk-index/bb = {h3}
        var blobs    = new FakeInMemoryBlobContainerService();
        var machineA = UniqueRepositoryKey("s3a-machineA");
        var first    = await WarmFirstRunAsync(blobs, machineA); // uploads "aa" + "bb" to the shared remote

        var machineB = UniqueRepositoryKey("s3a-machineB"); // different cache identity, SAME blobs
        blobs.RequestedBlobNames.Clear();
        blobs.UploadedBlobNames.Clear(); // ignore machine A's warm-up uploads

        // Act — machine B (empty cache, snapshot "s1") looks up the three hashes:
        //       Lookup(h1) → P3b (cache empty, so ETag miss) → TryDownload "aa" + deserialize
        //       Lookup(h2) → P1  ("aa" now loaded)
        //       Lookup(h3) → P3b → TryDownload "bb" + deserialize
        using var runB = NewRun(blobs, machineB, new FakeSnapshotService([Snapshot("s1")]));

        (await runB.LookupAsync(first.H1)).ShouldBe(first.E1); // P3b: empty cache → download "aa"
        (await runB.LookupAsync(first.H2)).ShouldBe(first.E2); // P1: "aa" now loaded
        (await runB.LookupAsync(first.H3)).ShouldBe(first.E3); // P3b: download "bb"

        // Assert — one download per touched prefix (not per hash), and machine B never writes.
        //   "aa" is downloaded once (serving both h1 and h2 from that single load); "bb" once; 0 uploads.
        blobs.RequestedBlobNames.Count(n => n == BlobPaths.ChunkIndexShardPath(first.PrefixAa)).ShouldBe(1);
        blobs.RequestedBlobNames.Count(n => n == BlobPaths.ChunkIndexShardPath(first.PrefixBb)).ShouldBe(1);
        ChunkIndexUploads(blobs).ShouldBe(0); // machine B only reads
    }

    // ── Scenario 3b: another machine, stale ETag → download refreshes the prefix from remote (P3b) ─────────────

    [Test]
    public async Task Scenario3b_AnotherMachineStaleEtag_DownloadsAndRefreshesFromRemote()
    {
        // Arrange — build a STALE local cache: machine B caches "aa" at one ETag, then machine A moves it.
        //   (1) Machine A archived → REMOTE "aa" = {h1,h2} @ etag A1, snapshot "s1".
        var blobs    = new FakeInMemoryBlobContainerService();
        var machineA = UniqueRepositoryKey("s3b-machineA");
        var first    = await WarmFirstRunAsync(blobs, machineA); // "aa" = {h1,h2}, snapshot s1

        //   (2) Machine B warms its OWN cache for "aa" at the current remote ETag (A1) / snapshot "s1".
        var machineB = UniqueRepositoryKey("s3b-machineB");
        using (var runB1 = NewRun(blobs, machineB, new FakeSnapshotService([Snapshot("s1")])))
            (await runB1.LookupAsync(first.H1)).ShouldBe(first.E1);

        //   (3) Machine A re-archives a new hash in "aa": this REWRITES the remote shard (new etag A2) and
        //       creates snapshot "s2". Machine B's cache is now stale — it still thinks "aa" is A1 / s1.
        var h5 = SamePrefix(first.H1, 'd');
        var e5 = new ShardEntry(h5, FakeChunkHash('5'), 50, 25, BlobTier.Cool);
        using (var runA2 = NewRun(blobs, machineA, new FakeSnapshotService([Snapshot("s1")])))
        {
            (await runA2.LookupAsync(h5)).ShouldBeNull();
            runA2.AddEntry(e5);
            await runA2.FlushAsync();                  // remote "aa" → new ETag
            await runA2.PromoteToSnapshotVersionAsync("s2");
        }

        //   State now:  REMOTE "aa" = {h1,h2,h5} @ A2 (snapshot "s2")   vs   MACHINE B CACHE "aa" @ A1 / s1.
        blobs.RequestedBlobNames.Clear(); // measure only machine B's next run

        // Act — machine B runs again, now seeing snapshot "s2", and looks up h5 (which it has never seen):
        //       snapshot moved (s1 ≠ s2) → TryDownload "aa" → ETag differs (A1 ≠ A2) → P3b → deserialize + refresh.
        using var runB2 = NewRun(blobs, machineB, new FakeSnapshotService([Snapshot("s1"), Snapshot("s2")]));
        var refreshed = await runB2.LookupAsync(h5); // a hash machine B has never seen locally

        // Assert — the stale prefix was refreshed from the remote.
        //   "aa" downloaded exactly once; h5 (only in the new remote shard) is now found; h1 still present.
        blobs.RequestedBlobNames.Count(n => n == BlobPaths.ChunkIndexShardPath(first.PrefixAa)).ShouldBe(1); // P3b
        refreshed.ShouldBe(e5);                                  // stale cache refreshed from remote
        (await runB2.LookupAsync(first.H1)).ShouldBe(first.E1);  // original entries still present after refresh
    }

    // ── Scenario 4: cheap revalidation — newer snapshot, but this prefix's shard is unchanged (P3a) ───────────

    [Test]
    public async Task Scenario4_CheapRevalidation_NewSnapshotShardUnchanged_OneProbeThenCached()
    {
        // Arrange — the realistic mirror image of 3b: machine B has "aa" cached, then a newer snapshot appears
        //   whose archive touched a DIFFERENT prefix, so "aa"'s shard (and its ETag) is left untouched.
        //   (1) Machine A archived → REMOTE "aa" = {h1,h2} @ etag A1, "bb" = {h3} @ B1, snapshot "s1".
        var blobs    = new FakeInMemoryBlobContainerService();
        var machineA = UniqueRepositoryKey("s4-machineA");
        var first    = await WarmFirstRunAsync(blobs, machineA);
        var aaEtagA1 = (await blobs.GetMetadataAsync(BlobPaths.ChunkIndexShardPath(first.PrefixAa))).ETag!;

        //   (2) Machine B warms its OWN cache for "aa" at the current remote ETag (A1) / snapshot "s1".
        var machineB = UniqueRepositoryKey("s4-machineB");
        using (var runB1 = NewRun(blobs, machineB, new FakeSnapshotService([Snapshot("s1")])))
            (await runB1.LookupAsync(first.H1)).ShouldBe(first.E1);

        //   (3) Machine A archives AGAIN, but only adds a chunk in a NEW prefix "cc" — it never rewrites "aa".
        //       This creates snapshot "s2" while "aa"'s shard (still ETag A1) stays exactly as machine B cached it.
        var h6 = FakeContentHash('c'); // prefix "cc"
        var e6 = new ShardEntry(h6, FakeChunkHash('6'), 60, 30, BlobTier.Cool);
        using (var runA2 = NewRun(blobs, machineA, new FakeSnapshotService([Snapshot("s1")])))
        {
            (await runA2.LookupAsync(h6)).ShouldBeNull();
            runA2.AddEntry(e6);
            await runA2.FlushAsync();                  // uploads only "cc"; "aa" is left untouched at ETag A1
            await runA2.PromoteToSnapshotVersionAsync("s2");
        }

        //   State now:  REMOTE "aa" = {h1,h2} @ A1 (UNCHANGED), "cc" = {h6} @ C1, latest snapshot "s2".
        //               MACHINE B CACHE "aa" @ A1 / s1  — correct ETag, but a stale snapshot stamp.
        blobs.RequestedBlobNames.Clear(); // measure only machine B's next run

        // Act — machine B runs again at snapshot "s2" and looks up h1 (in prefix "aa"):
        //       snapshot moved (s1 ≠ s2) → TryDownload "aa" → ETag matches (A1 == A1) → P3a:
        //       re-stamp "aa" as valid for "s2" and keep the cached rows (no body re-parse).
        using (var runB2 = NewRun(blobs, machineB, new FakeSnapshotService([Snapshot("s1"), Snapshot("s2")])))
        {
            (await runB2.LookupAsync(first.H1)).ShouldBe(first.E1);

            // Assert — the snapshot bump cost exactly ONE cheap probe for "aa"...
            blobs.RequestedBlobNames.Count(n => n == BlobPaths.ChunkIndexShardPath(first.PrefixAa)).ShouldBe(1);

            // ...and "aa" is now validated for "s2", so an immediate re-lookup is a pure P1 hit (0 downloads).
            blobs.RequestedBlobNames.Clear();
            (await runB2.LookupAsync(first.H1)).ShouldBe(first.E1);
            ChunkIndexDownloads(blobs).ShouldBe(0);
        }

        // White-box — "aa" was re-stamped to "s2" while keeping the unchanged remote identity A1 (revalidated, not reloaded).
        ClearPool(machineB);
        var store = new ChunkIndexLocalStore(RepositoryLocalStatePaths.GetChunkIndexCacheRoot(machineB, machineB));
        store.IsPrefixAtSnapshotVersion(first.PrefixAa, "s2").ShouldBeTrue();
        store.IsPrefixAtETag(first.PrefixAa, aaEtagA1).ShouldBeTrue();
    }

    // ── Scenario 5: flush failure → dirty rows survive → a later run re-flushes successfully ──────────────────

    [Test]
    public async Task Scenario5_FlushFailure_PreservesDirtyRows_AndRetryReuploads()
    {
        // Arrange — simulate a crashed run that leaves dirty rows behind. `faulting` wraps the in-memory store
        //   and throws on any UploadAsync to chunk-index/* (FailChunkIndexUploads). h1, h2 both in prefix "aa".
        var blobs    = new FakeInMemoryBlobContainerService();
        var faulting = new FaultingChunkIndexUploadBlobContainerService(blobs);
        var account  = UniqueRepositoryKey("s5-flush-retry");
        var h1 = FakeContentHash('a');
        var h2 = SamePrefix(h1, 'b');
        var e1 = new ShardEntry(h1, FakeChunkHash('1'), 10, 5, BlobTier.Cool);
        var e2 = new ShardEntry(h2, FakeChunkHash('2'), 20, 8, BlobTier.Cool);
        var prefixAa = Shard.PrefixOf(h1);

        //   Run A records both entries (persisted to SQLite as DIRTY rows), then its flush DIES on the upload:
        //   no MarkSynchronized, no promote, nothing reaches the remote.
        using (var runA = NewRun(faulting, account, new FakeSnapshotService([Snapshot("s1")])))
        {
            await runA.LookupAsync(h1);
            await runA.LookupAsync(h2);
            runA.AddEntry(e1);
            runA.AddEntry(e2);
            await Should.ThrowAsync<InvalidOperationException>(() => runA.FlushAsync());
        }

        blobs.UploadedBlobNames.ShouldBeEmpty(); // precondition confirmed: the crash published nothing

        // Act — a later run retries. Run B is a FRESH instance on the SAME machine over a NON-faulting store.
        //   The dirty rows persisted in SQLite, so the lookups still return them with no remote call, and the
        //   flush now uploads shard "aa" successfully.
        using (var runB = NewRun(blobs, account, new FakeSnapshotService([Snapshot("s1")])))
        {
            (await runB.LookupAsync(h1)).ShouldBe(e1); // dirty row preserved across the failed flush
            (await runB.LookupAsync(h2)).ShouldBe(e2);
            await runB.FlushAsync();
        }

        // Assert — the retry recovered cleanly: the merged shard { h1, h2 } is now on the remote (1 upload).
        ChunkIndexUploads(blobs).ShouldBe(1); // the retry uploaded the merged shard
        (await ReadRemoteShardAsync(blobs, prefixAa)).Entries.Select(e => e.ContentHash).ShouldBe([h1, h2], ignoreOrder: true);
    }

    // ── Scenario 6: no-op run — fully warm cache, everything deduped, no remote calls ─────────────────────────

    [Test]
    public async Task Scenario6_NoOpRun_FullyWarmCache_MakesNoRemoteCalls()
    {
        // Arrange — a fully warm machine. WarmFirstRunAsync left REMOTE = { aa:{h1,h2}, bb:{h3} } and a LOCAL
        //   CACHE with every prefix clean at snapshot "s1". Nothing has changed since; trackers cleared.
        var blobs   = new FakeInMemoryBlobContainerService();
        var account = UniqueRepositoryKey("s6-noop");
        var first   = await WarmFirstRunAsync(blobs, account);

        blobs.RequestedBlobNames.Clear();
        blobs.UploadedBlobNames.Clear(); // ignore the warm-up run's uploads

        // Act — re-run the archive with the SAME content and nothing new:
        //       Lookup(h1/h2/h3) → all P1 (warm cache at latest snapshot "s1") → dedup hits, no AddEntry.
        //       FlushAsync finds 0 dirty prefixes → early return (no upload).
        var snapshot = new FakeSnapshotService([Snapshot("s1")]);
        using (var run = NewRun(blobs, account, snapshot))
        {
            (await run.LookupAsync(first.H1)).ShouldBe(first.E1);
            (await run.LookupAsync(first.H2)).ShouldBe(first.E2);
            (await run.LookupAsync(first.H3)).ShouldBe(first.E3);
            await run.FlushAsync(); // 0 dirty prefixes → early return
        }

        // Assert — a no-op run touches the network zero times.
        //   0 downloads, 0 uploads; the snapshot list was resolved exactly once (cached AsyncLazy).
        ChunkIndexDownloads(blobs).ShouldBe(0);
        ChunkIndexUploads(blobs).ShouldBe(0);
        snapshot.ListBlobNamesCallCount.ShouldBe(1); // snapshot list resolved at most once (AsyncLazy)
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Performs a first archive run (three new chunks across prefixes "aa" and "bb") and promotes to s1.</summary>
    private static async Task<FirstRun> WarmFirstRunAsync(FakeInMemoryBlobContainerService blobs, string account)
    {
        var h1 = FakeContentHash('a');
        var h2 = SamePrefix(h1, 'b');
        var h3 = FakeContentHash('b');
        var e1 = new ShardEntry(h1, FakeChunkHash('1'), 10, 5, BlobTier.Cool);
        var e2 = new ShardEntry(h2, FakeChunkHash('2'), 20, 8, BlobTier.Cool);
        var e3 = new ShardEntry(h3, FakeChunkHash('3'), 30, 12, BlobTier.Cool);

        using var run = NewRun(blobs, account, new FakeSnapshotService());
        await run.LookupAsync(h1);
        await run.LookupAsync(h2);
        await run.LookupAsync(h3);
        run.AddEntry(e1);
        run.AddEntry(e2);
        run.AddEntry(e3);
        await run.FlushAsync();
        await run.PromoteToSnapshotVersionAsync("s1");

        return new FirstRun(h1, h2, h3, e1, e2, e3, Shard.PrefixOf(h1), Shard.PrefixOf(h3));
    }

    private sealed record FirstRun(ContentHash H1, ContentHash H2, ContentHash H3, ShardEntry E1, ShardEntry E2, ShardEntry E3, PathSegment PrefixAa, PathSegment PrefixBb);

    private static ChunkIndexService NewRun(IBlobContainerService blobs, string repositoryKey, FakeSnapshotService snapshot)
        => new(blobs, s_encryption, snapshot, repositoryKey, repositoryKey);

    /// <summary>A content hash sharing <paramref name="hash"/>'s shard prefix but filled with <paramref name="fill"/>.</summary>
    private static ContentHash SamePrefix(ContentHash hash, char fill)
        => ContentHash.Parse($"{hash.Prefix(ChunkIndexService.ShardPrefixLength)}{new string(fill, 64 - ChunkIndexService.ShardPrefixLength)}");

    private static RelativePath Snapshot(string name) => BlobPaths.SnapshotPath(name);

    private static int ChunkIndexDownloads(FakeInMemoryBlobContainerService blobs)
        => blobs.RequestedBlobNames.Count(n => n.StartsWith(BlobPaths.ChunkIndexPrefix));

    private static int ChunkIndexUploads(FakeInMemoryBlobContainerService blobs)
        => blobs.UploadedBlobNames.Count(n => n.StartsWith(BlobPaths.ChunkIndexPrefix));

    private static async Task<Shard> ReadRemoteShardAsync(IBlobContainerService blobs, PathSegment prefix)
    {
        var download = await blobs.DownloadAsync(BlobPaths.ChunkIndexShardPath(prefix), CancellationToken.None);
        await using var stream = download.Stream;
        return ShardSerializer.Deserialize(stream, s_encryption);
    }

    private static string UniqueRepositoryKey(string name) => $"acct-{name}-{Guid.NewGuid():N}";

    private static void ClearPool(string repositoryKey)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(repositoryKey, repositoryKey).Resolve(RelativePath.Parse("cache.sqlite")),
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Pooling    = true,
        }.ToString());

        SqliteConnection.ClearPool(connection);
    }
}
