---
status: "accepted"
date: 2026-06-17
decision-makers: ["Wouter Van Ranst"]
consulted: ["Claude Code"]
informed: ["Arius maintainers"]
confidence: "medium"
---

# Anchor multi-machine cache coherence on the snapshot epoch, not distributed locks

## Context and Problem Statement

A single Arius repository (one Azure Blob container) can be archived from more than one machine, and each machine keeps a local cache of repository metadata under `~/.arius/{account}-{container}/`: snapshot manifests, content-addressed filetree blobs, and the chunk-index that maps content hashes to chunks (`ShardEntry` rows). After one machine archives, another machine's cache is potentially behind. The cache must never let a machine reuse stale local state in a way that corrupts the index or fails a later restore, yet it must also avoid paying a full remote round-trip on every operation when nothing changed.

The two cached blob families have opposite freshness semantics. Filetree and snapshot blobs are content-addressed and immutable — a cached file is correct by definition or absent. Chunk-index shard blobs (`chunk-index/{prefix}`) are mutable: `ChunkIndexService.FlushAsync` rewrites whole shards and `SplitShardAsync` deletes and replaces them, so another machine can publish a newer shard at the same name. Arius also explicitly does not coordinate concurrent archives (`design.md` non-goal: "Do not introduce distributed locking or concurrent archive coordination").

The question for this ADR is what coherence model lets a machine trust its local cache against a shared repository that other machines mutate, without distributed locks or two-phase commit.

## Decision Drivers

* Restores must stay correct: a stale cache must never let a published snapshot reference an entry the index cannot resolve.
* The common case (same machine archives repeatedly) must avoid remote listings and re-downloads.
* Mutable chunk-index shards require freshness proof; immutable content-addressed blobs do not.
* No distributed locking, leasing, or 2PC — Arius is a personal/small-team backup tool, not a coordination service.
* Concurrent writers are tolerated, not prevented; the system must converge afterwards.
* In-flight archive state (newly uploaded chunks not yet flushed) must never be discarded by a coherence check.

## Considered Options

* Anchor coherence on the latest snapshot as an epoch marker, with per-prefix ETag validation for mutable shards and permanent trust for immutable blobs.
* Acquire a per-repository distributed lease (e.g. a blob lease) so only one machine writes at a time.
* Re-list and re-download all repository metadata at the start of every operation (no trust).
* Trust the local cache unconditionally and reconcile only via explicit repair.

## Decision Outcome

Chosen option: "Anchor coherence on the latest snapshot as an epoch marker, with per-prefix ETag validation for mutable shards and permanent trust for immutable blobs", because the snapshot is already the repository's single commit point (it is the last thing an archive publishes), it lets the common same-machine path skip all remote listing, it scopes the expensive work to exactly the mutable data, and it accepts last-writer-wins with repair as the recovery path rather than paying for distributed coordination Arius does not need.

Confidence: medium. The model is implemented and tested for the validation and invalidation mechanics, but the concurrent-writer tolerance is a deliberately accepted known limitation rather than a proven-convergent protocol: two machines flushing overlapping shard rewrites can lose one writer's rewrite (last writer wins), recovered only by `RepairAsync`. Evidence that could revisit this: real multi-writer corruption that repair cannot recover, which would justify ETag-conditional shard writes.

Before — no shared coherence point; existence was probed per blob (HTTP HEAD per tree node) and there was no per-prefix freshness proof for mutable shards.

After — the latest snapshot name is the epoch. `FileTreeService.ValidateAsync` compares the latest local snapshot to the latest remote snapshot once per archive:

```text
latest local snapshot == latest remote snapshot ?
  ├─ yes → FAST PATH: this machine was last writer; local cache fully trusted, no listing
  └─ no  → SLOW PATH: list filetrees/, materialize empty marker files,
            return SnapshotMismatch=true → ArchiveCommandHandler calls
            ChunkIndexService.InvalidateCaches() (drops clean rows + run-scoped listing,
            preserves dirty rows) before FlushAsync
```

Mutable chunk-index shards are then trusted per-prefix only after validation against the current snapshot identity plus the remote blob ETag (`ChunkIndexLocalStore.IsPrefixAtETag`); immutable filetree/snapshot blobs are trusted permanently (a cached file is its own SHA-256 hash).

### Snapshot as the epoch (SnapshotService)

`SnapshotService.CreateAsync` is write-through: it writes the manifest JSON to the local `snapshots/` cache first, then uploads to Azure. The timestamp filename (`TimestampFormat = "yyyy-MM-ddTHHmmss.fffZ"`) sorts lexicographically as chronologically, so "latest" is a string sort on both sides. Because the local marker is only written when this machine completes an archive, "latest local == latest remote" is exactly the proposition "this machine wrote the last snapshot" — the fast-path condition in `ValidateAsync`.

### FileTreeService validation epoch (fast/slow path)

`FileTreeService.ValidateAsync` runs once per archive (stage 6a in `ArchiveCommandHandler`), guarded by `_validated` for idempotency:

* **Fast path** — `latestLocal == latestRemote` (or no remote snapshots): returns `SnapshotMismatch=false` with no `filetrees/` listing. The disk cache is complete and trusted.
* **Slow path** — names differ or there is no local snapshot: lists `filetrees/` once and writes a zero-byte marker file for every remote blob not already on disk, then returns `SnapshotMismatch=true`. Markers make `ExistsInRemote` a pure local `File.Exists` check for the rest of the build (it throws if called before `ValidateAsync`). Filetree blobs are immutable, so one listing yields a stable known-existing set for the epoch and the cache is never invalidated — only completed.

`ArchiveCommandHandler` responds to `SnapshotMismatch` by calling `_chunkIndex.InvalidateCaches()` before `FlushAsync`, so stale local shard rows cannot overwrite newer remote shards.

### Why mutable shards require validation (ChunkIndexService)

Unlike tree blobs, `chunk-index/{prefix}` blobs are mutable, so a cached `ShardEntry` row can be stale even though the snapshot epoch matched. `ChunkIndexLocalStore` therefore records per-range coverage claims in `loaded_prefixes`, each holding the snapshot version it was validated at and the remote blob ETag last seen. On lookup, `ChunkIndexService.EnsureCoverageCoreAsync` resolves each hash to its authoritative shard from a run-scoped `chunk-index/` listing, then `LoadShardAsync` revalidates without download when `IsPrefixAtETag` matches the listed ETag, and downloads + deserializes only on an ETag difference or a cache miss. Routine snapshot changes do not purge the repository; only touched prefixes are revalidated lazily.

`PromoteToSnapshotVersionAsync` carries already-validated coverage claims forward to a newly published snapshot version so the next run does not re-validate prefixes that did not change.

### Invalidation preserves dirty rows

`ChunkIndexService.InvalidateCaches` calls `ChunkIndexLocalStore.ClearRemoteBackedCache`, which deletes only `chunk_index_entries WHERE pending_flush = 0` and all `loaded_prefixes`, and resets the run-scoped shard listing (`_shardListing.Reset()`). Dirty rows (`pending_flush = 1`) — chunks uploaded this run but not yet flushed — always survive. Coherence invalidation can therefore never lose in-flight archive work; it only drops discardable hydrated cache state that will be re-validated from remote on next use.

### Concurrent writers: last-writer-wins, repair recovers

There are no leases or 2PC. Two machines archiving into the same repository can overwrite each other's shard rewrites — last writer wins (a documented, pre-existing limitation). The flush protocol is crash-safe for a single writer: `SplitShardAsync` uploads all non-empty leaf shards before deleting the parent, and dirty rows stay `pending_flush = 1` until every touched subtree uploads successfully, so an interrupted or losing run re-resolves the parent and re-splits on retry. Whole-repository convergence after concurrent writers is the job of `RepairAsync`, which rebuilds every shard from the authoritative chunk blobs and deletes all other `chunk-index/` blobs. ETag-conditional shard writes are a possible future hardening, not part of this decision.

### Consequences and Tradeoffs

* Good, because the same-machine repeat-archive path takes the fast path: one `snapshots/` list, no `filetrees/` list, no shard re-download.
* Good, because coherence cost is scoped to mutable data — immutable content-addressed blobs are trusted forever, mutable shards are validated per touched prefix by ETag.
* Good, because the snapshot is a single, already-existing commit point, so the epoch needs no new coordination artifact.
* Good, because invalidation can never destroy in-flight archive state — `pending_flush = 1` rows always survive `ClearRemoteBackedCache`.
* Bad, because concurrent archives from two machines can lose one writer's shard rewrite (last writer wins); correctness is restored only by running `RepairAsync`, not prevented up front.
* Bad, because a crash between snapshot upload and local marker write costs a spurious slow path next run (a re-list and shard revalidation) — a performance cost, not a correctness bug.
* Bad, because the run-scoped shard listing reuse depends on a load-bearing single-writer invariant: nothing may mutate remote `chunk-index/` blobs between dedup and flush except this machine's own `FlushAsync`.

### Confirmation

This decision is confirmed when:

* `FileTreeService.ValidateAsync` performs no `filetrees/` listing when the latest local and remote snapshot names match, and materializes markers + returns mismatch otherwise — `ValidateAsync_SnapshotMatch_NoFiletreesListing` and `ValidateAsync_SnapshotMismatch_MarkerFilesCreated_AndReturnsMismatch` in `FileTreeServiceTests`.
* `ExistsInRemote` throws before `ValidateAsync` has run (the epoch guard) — enforced by the `_validated` check in `FileTreeService`.
* `InvalidateCaches` forces a fresh remote listing on the next lookup — `InvalidateCaches_ForcesFreshListingOnNextLookup` in `ChunkIndexServiceListingCacheTests`.
* `ClearRemoteBackedCache` deletes clean rows and loaded-prefix claims but preserves dirty rows — `ClearRemoteBackedCache_PreservesPendingFlushRows_AndClearsLoadedPrefixes` in `ChunkIndexLocalStoreTests`.
* Per-prefix ETag revalidation reuses the cache without re-ingesting, and snapshot promotion carries claims forward — `IngestCoverage_RevalidatedPrefix_UpdatesSnapshotVersionWithoutReingest` and `PromoteToSnapshotVersion_UpdatesMatchingPrefixes_AndPreservesRemoteState` in `ChunkIndexLocalStoreTests`.
* `ArchiveCommandHandler` calls `ChunkIndexService.InvalidateCaches()` on `SnapshotMismatch` before `FlushAsync` (stage 6a → 6b).

This ADR records the coherence/validation model. It is distinct from the chunk-index sharding/scale decision (ADR-0015), which governs shard prefix layout, splitting, and `MaxShardEntryCount`; this ADR governs when a cached shard or tree may be trusted across machines.

## Pros and Cons of the Options

### Snapshot-epoch coherence with per-prefix ETag validation

The chosen design.

* Good, because it reuses the snapshot as a free commit point and makes the same-machine path nearly listing-free.
* Good, because it matches trust strategy to data: permanent trust for immutable blobs, ETag-validated per-prefix trust for mutable shards.
* Good, because dirty in-flight state is structurally protected from invalidation.
* Neutral, because it accepts last-writer-wins rather than preventing concurrent writes.
* Bad, because whole-repository convergence after a concurrent-writer collision requires an explicit `RepairAsync`.

### Per-repository distributed lease

Take a blob lease so only one machine archives at a time.

* Good, because it would prevent concurrent shard-rewrite collisions outright.
* Bad, because it adds a coordination protocol (lease acquisition, renewal, expiry, takeover) Arius explicitly lists as a non-goal.
* Bad, because a crashed lease holder blocks or forces risky lease-break logic, hurting a personal-backup tool's robustness.

### Re-list and re-download everything per operation (no trust)

* Good, because it is always correct with no epoch reasoning.
* Bad, because it pays a full `filetrees/` and `chunk-index/` listing plus shard downloads on every archive, ls, and restore — the exact cost the cache exists to remove.

### Trust local cache unconditionally, reconcile only via repair

* Good, because it is the cheapest possible read path.
* Bad, because a machine could publish a snapshot referencing entries another machine's shard rewrite already moved, risking unrestorable snapshots until a manual repair — unacceptable for a backup tool.

## More Information

* FileTree service fast/slow path and cross-service invalidation rationale: `docs/history/openspec-archive/2026-04-05-file-tree-service/design.md`.
* Chunk-index responsibility split, mutable-shard validation, and single-writer / last-writer-wins constraints: `docs/history/openspec-archive/2026-06-02-split-chunk-index-responsibilities/design.md`.
* Caching architecture overview: [`design/core/shared/snapshot.md`](../design/core/shared/snapshot.md), [`filetree.md`](../design/core/shared/filetree.md), and [`chunk-index.md`](../design/core/shared/chunk-index.md); service lifetime/scoping in [`design/cross-cutting/service-lifetimes.md`](../design/cross-cutting/service-lifetimes.md).
