# ChunkIndex scalability — issue #114 items #2, #7, #9

## Context

Arius stores deduplicated chunks in Azure Blob Storage. The dedup index (`content-hash → storage-chunk`) lives in *shard* blobs `chunk-index/{prefix}`, grouped by a dynamic-length hex prefix (256 fixed 2‑hex roots, recursively split 16‑way per root when a shard exceeds `MaxShardEntryCount` at flush), with a local SQLite cache (`ChunkIndexLocalStore`). Per `docs/cache.md` the operating assumption is **one reader/writer at a given time** (cross-machine concurrency is a conceded last-writer-wins limitation that repair recovers).

At ~1M+ chunks three bottlenecks bite (this work targets them; correctness is paramount and the work is test-driven):

- **#2** — `EnsureCoverageCoreAsync` re-lists the *whole root subtree* every time a lookup touches an **uncovered** leaf. Only coverage *claims* (`loaded_prefixes` rows) are cached, not the layout listing. Archive dedup does **per-hash single-`LookupAsync`**, so each newly-touched leaf re-lists its entire root. (`ChunkIndexService.cs:230`, `305-311`)
- **#7** — a brand-new repo (`_latestSnapshotName == "<none>"`) still drives a per-root listing per touched root, all returning empty. (`ChunkIndexService.cs:69-75`, `150-162`)
- **#9** — within one root, shard downloads in the coverage loop are **sequential** (`ChunkIndexService.cs:252-292`).

Intended outcome: lookups across a run perform **one cached `chunk-index/` listing**, cold-cache shard downloads run **in parallel**, and new/empty repos do **near-zero** remote work — all without weakening the crash-safety / parent-wins / last-writer-wins guarantees the current design relies on.

This plan and its design choices were **adversarially reviewed** against the real code (5 independent reviewers); the refinements below incorporate every confirmed hole.

---

## Formal analysis (answers to the investigation questions)

### Why the index is listed multiple times today
The SQLite cache persists *coverage claims* (`loaded_prefixes`: prefix → remote_exists + etag + snapshot_version), **not the layout listing**. `EnsureCoverageCoreAsync` re-derives the layout with a fresh `ListShardSubtreeAsync(root)` every time any hash in that root is *uncovered*. Under the documented sole-writer assumption the remote layout is **stable for the whole run**, so re-deriving it per uncovered leaf is pure waste. A run-scoped cached listing is the correct fix.

### How the pipelines use the index (small ~1k vs large ~1M)

| Pipeline | Call site | Pattern | Small (~1k) cold | Large (~1M) cold, today |
|---|---|---|---|---|
| archive (dedup) | `ArchiveCommandHandler.cs:367` | **per-hash single** `LookupAsync` | ~all 256 roots listed once each (≈256 lists) | up to **one list per touched leaf** (≈4096 lists) — the #2 blow-up |
| restore | `RestoreFilePipeline.cs:159` | batch `LookupAsync`, `ResolveBatchSize=32` | grouped by root, parallel across roots | one list per new root/leaf **per batch** |
| ls | `ListQueryHandler.cs:199` | batch per directory | one list per touched root | same |

`LookupAsync(batch)` groups by root and runs `Parallel.ForEachAsync` across roots (`PrefixLoadWorkers=8`); within a root, downloads are sequential. Archive's single-hash path has no batch parallelism at all.

### Listing cost model and the "one cached listing" decision (CHANGE A)
Azure `GetBlobsAsync` returns ≤5000 blobs/page and **auto-pages** via continuation token. So **one `ListAsync("chunk-index/")` enumerates every shard** at cost `ceil(#shards/5000)` billed List ops (≈1 for our scales), paid **once per run** and reused. This is strictly cheaper than per-2-char (≤256 lists) or per-leaf (thousands) listing. **Decision: cache one full `chunk-index/` listing per run.** It also subsumes #7 (empty repo → empty listing → all misses with one list, zero downloads).

### Per-1-char vs full listing
Sharding the listing into 16 (`chunk-index/0…f`) or 256 (`…/00…ff`) prefixes is **unnecessary** — a single full listing auto-pages and returns everything in fewer requests. Per-char would only help to *parallelize paging* of a >5000-shard index (>~4.2M chunks at T=1024); not worth the complexity now.

### MaxShardEntryCount — corrected analysis and decision: **keep 1024**
Adversarial review corrected my earlier numbers:
- Per entry: ~87 B (large, 4 fields) / ~148 B (small, 5 fields) plaintext → **~44 B compressed** (zstd‑19; dominated by 32 B incompressible hash entropy). `ShardEntry.Serialize` (Shard.cs:30-33), zstd‑19 (`ZstdCompressionService`).
- Shard size: **T=1024 leaf ≈ 11 KB**, T=4096 unsplit root ≈ **170 KB** (not 270 KB).
- Cold-dedup downloads ~the entire index regardless of T → **total bytes ~constant (~43–45 MB at 1M)**; T only changes the GET **count** (256 vs 4096), which **CHANGE B (parallel downloads) addresses directly**.
- **Incremental flush rewrites the *whole* touched shard** (`BuildShard` + `UploadShardAsync` with `overwrite:true`, no delta — `ChunkIndexService.cs:463-488`). So write-amplification scales with shard size and **dominates steady state**: a daily archive touching ~200 roots writes ~2.2 MB at T=1024 vs ~34 MB at T=4096 (~15×).

Conclusion: optimize the **common incremental path**, not the once-per-machine cold path. **Keep `MaxShardEntryCount = 1024`.** Design point **~1.3M chunks → ~4096 shards → one 5000-blob list page** (≈900 shards of headroom). `PartitionIntoLeaves` recurses until each group fits, so leaves fill *toward* T (not T/16).

### Gradual failure mode beyond ~5000 shards (confirmed)
Correct — purely additive: cost is `ceil(#shards/5000)` sequential list round-trips per run (cached thereafter). No cliff, no error, no correctness impact. At T=1024 the layout stays ~4096 shards (1 page) from ~262k up to ~4.2M chunks; second-level splits past ~4.2M push toward more pages, +1 page per +5000 shards. Secondary linear effects: cached-listing memory (~55 B/shard: 5000≈275 KB, 65k≈3.6 MB). The only **non-gradual** watch-item is **#5** (`ResolveTarget`'s per-hash `.Any(StartsWith)` scan grows with shards-per-root) — out of scope here.

---

## Recommended approach

Implement in order **A → B → C**, each landing test-first (red → green), as separate commits.

### CHANGE A — single run-scoped cached `chunk-index/` listing (fixes #2 and #7)

Replace per-root on-demand `ListShardSubtreeAsync(root)` in the **coverage/dedup path** with one cached full listing.

- Add a **generation-guarded, atomically-swappable** cached listing of the entire `chunk-index/` prefix (name → ETag), populated by one `_blobs.ListAsync(BlobPaths.ChunkIndexPrefix, BlobListPrefixKind.DirectoryPrefix, …)`. Use a `Lazy<Task<…>>` (or `AsyncLazy`) stored in a field swapped under an `Interlocked` generation counter — **never expose a half-populated dictionary** to the 8 lookup / 32 flush workers (reviewer-confirmed hazard).
- `EnsureCoverageCoreAsync` consults the cached listing, **filtered to the root's range** once per call (preserve current per-hash `ResolveTarget` cost; pass the root-filtered name set, not the global set, so #5 doesn't regress).
- **Empty/new repo (#7):** empty listing → every uncovered hash routes `Exists:false` → `AddEmptyPrefix` → covered. One list, zero downloads for the whole run. This is the **correct** #7 fix: discovery is by **blob existence**, not snapshot state, so it also finds **orphan shards** left by a crash between flush (stage 6b) and snapshot create (stage 6d). *Reject the naive "skip remote lookups when snapshot==`<none>`" idea — it is a data-loss vector: skipping the listing skips parent-wins merge at flush, and `overwrite:true` then drops orphan entries the run didn't re-touch.*
- **Reset hooks (load-bearing):**
  - `InvalidateCaches()` must reset the cached listing (snapshot-mismatch at `ArchiveCommandHandler.cs:606-608` → flush re-lists fresh).
  - On a download 404 race, reset + re-fetch the listing **once** (generation-guarded so a single re-list serves all racing workers — no storm).
- **Flush coverage** (`FlushRootAsync` → `EnsureCoverageCoreAsync`) reuses the cached listing (correct under sole-writer: the root is untouched until its own flush).
- **`SplitShardAsync` stale-delete scan keeps a FRESH listing** (`ChunkIndexService.cs:450`). Splits are rare and deletion is destructive — leave these exact semantics unchanged; do **not** serve the delete-scan from the cached listing.
- **Document** as a load-bearing invariant (in `docs/cache.md`): nothing mutates `chunk-index/` remote blobs between dedup (stage 3) and flush (stage 6b) except `FlushAsync` itself; any future mutation in that window must reset the cached listing.

Correctness argument: under sole-writer, dedup and flush results are **identical** to today (a stale-listing dedup mis-decision can only cause an idempotent re-upload — a false "exists" still resolves to 404/empty → `null` → upload, never a false dedup-hit that drops a chunk). Under concurrent writers, divergence stays inside the already-documented last-writer-wins envelope, and `InvalidateCaches` on epoch mismatch forces a fresh listing.

### CHANGE B — parallel shard downloads with batched local-store write (fixes #9)

In `EnsureCoverageCoreAsync`, parallelize the **network/deserialize** work but **serialize the SQLite write**:

- Download + deserialize the distinct `shardsToLoad` prefixes with bounded concurrency (`Parallel.ForEachAsync` at `PrefixLoadWorkers`), **collecting** `(prefix, etag, entries)` and the empty-prefix set — do **not** write to the store mid-fan-out.
- Apply all results in **one batched transaction** via a new `ChunkIndexLocalStore` method (e.g. `IngestPrefixes(updates, emptyPrefixes, snapshotVersion)`) under `_localStateGate`. Safe because `ResolveTarget`'s parent-wins walk guarantees `shardsToLoad` prefixes are **pairwise non-nested**, so the `UpsertLoadedPrefix` overlap-deletes cannot race.
- **Why not just parallelize the writes:** there is **no `busy_timeout` pragma** on the per-call connections; Microsoft.Data.Sqlite retries `SQLITE_BUSY` with `Thread.Sleep(150)` capped at 30 s `CommandTimeout`. Raising raw write concurrency risks thread-pool starvation and 30 s-cap failures. Batching into one transaction removes inter-write contention entirely. (Consider also setting an explicit `PRAGMA busy_timeout` / `Default Timeout` on `OpenConnection` as defense-in-depth — the cross-connection dedup-vs-`UpsertPendingFlush` contention is already live in archive today.)
- **Race-retry:** make `retriedListing` a **single shared latch** for the whole fan-out. Run the parallel pass; collect 404'd prefixes (do **not** `AddEmptyPrefix` mid-pass); if any and not yet retried, reset the cached listing, re-list, **re-resolve from the fresh listing once** (so a split-away prefix isn't marked empty against a stale listing); on the second pass a still-404 prefix becomes `AddEmptyPrefix`. Bounded to exactly one re-list.

### CHANGE C — MaxShardEntryCount (no value change; formalize + pin)

- Keep `MaxShardEntryCount = 1024` (`ChunkIndexService.cs:19`).
- Add the corrected analysis + the ~1.3M design point + the gradual-page-cost note to `docs/cache.md`.
- Add a **regression test** pinning #shards/leaf-count for a known entry count at T (e.g. with the test-overridable `maxShardEntryCount`), so future threshold changes are deliberate.

---

## Critical files

- `src/Arius.Core/Shared/ChunkIndex/ChunkIndexService.cs` — cached listing field + reset hooks; `EnsureCoverageCoreAsync` (consult cache, parallel downloads, batched write, shared-latch retry); leave `SplitShardAsync:450` fresh.
- `src/Arius.Core/Shared/ChunkIndex/ChunkIndexLocalStore.cs` — new batched `IngestPrefixes(...)` (one transaction, under `_localStateGate`); optional `busy_timeout` on `OpenConnection`.
- `src/Arius.Core/Shared/ChunkIndex/ChunkIndexRouter.cs` — `ResolveTarget` unchanged (relied on for the non-nesting invariant; add an intent comment).
- `docs/cache.md` — listing-cache invariant; MaxShardEntryCount rationale; in-process concurrent-writer model.
- Reuse: `BlobPaths.ChunkIndexPrefix`, `IBlobContainerService.ListAsync` (auto-pages, returns ETags), `FakeInMemoryBlobContainerService.ListedNamePrefixes/RequestedBlobNames`, `CountingBlobContainerService.ChunkIndexLists/TryDownloads/Uploads`.

---

## Test plan (TUnit; correctness-first)

Decide `BlobNamePrefix` vs `DirectoryPrefix` for the full listing and route every assertion through a shared `ExpectedFullListPrefix` helper (avoid literals).

**Breaking assertions to update (they encode the old per-root behavior):**
- `ChunkIndexServiceLookupTests.cs:26` and `:220` — `["chunk-index/aa"]` → single full-prefix value.
- `ChunkIndexServiceArchiveScenarioTests.cs:123` (unit) — `ListedNamePrefixes.Count` `2 → 1`; update banner comment.
- `Arius.Integration.Tests/ChunkIndex/ChunkIndexServiceArchiveScenarioTests.cs:67` — `ChunkIndexLists` `2 → 1`.
- Re-verify (likely survive, update intent comments): unit `…ArchiveScenarioTests.cs:339`, `LookupTests.cs:483, 463-466`. (`FakeInMemoryBlobContainerServiceTests.cs:41` exercises the fake directly — unaffected.)

**New correctness tests (make #2/#7/#9 provable):**
1. **#2 single-listing reuse across roots:** one instance, `Lookup(root aa)` cold → 1 full list; `ClearListedNamePrefixes`; `Lookup(root bb)` → `ListedNamePrefixes.ShouldBeEmpty()` and bb resolves.
2. **#7 orphan-under-`<none>`:** seed remote `chunk-index/aa={h}`, empty cache, `FakeSnapshotService()` (`<none>`); `Lookup(h)` must FIND h (1 list + 1 download) — proves no snapshot shortcut.
3. **#7 empty repo:** empty remote + cache + `<none>`; batched lookup spanning several roots → `ListedNamePrefixes.Count==1`, downloads==0.
4. **#9 parallel distinct shards:** cold cache, one root split into leaves `aa0..aaf`; batched lookup (one hash/leaf) → each leaf downloaded exactly once, every entry resolves equal, none dropped/duplicated.
5. **InvalidateCaches resets listing:** `Lookup` (1 list) → `InvalidateCaches()` → next op performs a **second** full list (`ChunkIndexLists==2`), mirroring handler order.
6. **lostListingRace under parallel + cached:** inject a mid-run delete/404 of one listed shard; assert exactly **one** re-list (two full listings) and correct final resolution of the raced prefix.
7. **split-during-run delete correctness:** a flush that splits computes its delete set from a FRESH listing (incl. this run's uploads); `FlushAsync_Split_DeletesForeignStaleChildInRange` and `FlushAsync_InterruptedSplit…` still pass for the right reason. Add a case seeding an in-range stale blob *after* the run-start listing to pin the documented boundary.
8. **same-root parallel write safety:** seed many leaves under one root; cold batched lookup loading siblings in parallel while a second thread `AddEntries` the same root → no `SqliteException`, consistent claims/entries.
9. **CHANGE C pin:** known entry count → expected leaf-shard count at T.

---

## Verification (end-to-end)

1. Unit: run the ChunkIndex unit suite (TUnit treenode-filter per memory) — all new + updated tests green.
2. Integration (Azurite + `CountingBlobContainerService`): a full archive→ls→restore cycle asserts `ChunkIndexLists` is **O(1)** per run (was O(touched leaves/roots)); empty-repo archive lists once, downloads zero; second-machine cold path lists once.
3. Scaled run: seed ~100k–1.3M synthetic entries; confirm ~4096 shards / one list page, parallel cold-dedup wall-time improvement, and bounded incremental-flush upload bytes.
4. Local coverage via coverlet.console (`--include "[Arius.Core]*"`, per memory) over the ChunkIndex suite.
