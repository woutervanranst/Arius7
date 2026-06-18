---
status: "accepted"
date: 2026-06-17
decision-makers: ["Wouter Van Ranst"]
consulted: ["Claude Code"]
informed: ["Arius maintainers"]
confidence: "medium"
---

# Scale the chunk-index via dynamic-length prefix sharding

## Context and Problem Statement

The chunk-index is Arius's deduplication index: it maps a file's content hash to its stored chunk hash, original size, stored chunk size, and storage-tier hint (`ShardEntry` in `Arius.Core.Shared.ChunkIndex`). It is consulted on every archived file to decide whether a chunk already exists, and rewritten on every archive. Over three evolution stages it went from a fully in-memory index, to a service with split read/write responsibilities, to a disk-backed SQLite cache. The remaining problem is the *remote layout*: a single index blob, or a fixed flat set of prefix blobs, does not scale. A single blob must be fully rewritten on every archive; a fixed-width prefix either wastes round-trips at small repositories or produces oversized shards at large ones. Because incremental flush rewrites the *whole* touched shard blob, the per-shard size directly drives steady-state write-amplification.

The question for this ADR is how the chunk-index remote layout should grow with repository size while keeping the steady-state incremental archive cheap.

## Decision Drivers

* Steady-state cost is the daily incremental archive that touches a small number of shards; the cold full-rebuild path runs at most once per machine.
* Incremental flush rewrites the entire touched shard blob, so write-amplification scales with shard size, not with the number of changed entries.
* The layout must work for both a tiny first archive and a multi-million-chunk repository without operator tuning.
* Blob listing should stay within a single Azure list page (5000 blobs) for the realistic design point so layout discovery is one round-trip.
* The layout must survive an interrupted flush: a crash mid-split must not make a published snapshot's chunks unresolvable.
* No layout manifest blob to keep consistent — the layout should be derivable from what exists.

## Considered Options

* Single chunk-index blob rewritten on every archive.
* Fixed-width prefix sharding (a static set of `N`-hex-character shard blobs).
* Dynamic-length prefix sharding: 2-hex roots that split 16-way by the next hex character when a shard exceeds `MaxShardEntryCount`.

## Decision Outcome

Chosen option: "Dynamic-length prefix sharding", because it keeps shards bounded in size (so incremental write-amplification stays low) while adapting depth to repository size with no manifest and no operator tuning, and because the parent-wins routing makes an interrupted split safe.

Shards are named by a hex prefix of the content hash. The root depth is fixed at `MinShardPrefixLength = 2` (256 roots, e.g. `chunk-index/aa`). When a shard's merged entry count exceeds `MaxShardEntryCount = 1024` at flush time, `ChunkIndexRouter.PartitionIntoLeaves` splits it 16-way by the next hex character (`aa` → `aa0`..`aaf`), recursively and unevenly per subtree (`aaf` may later split into `aaf0`.. while `ab` never splits). Only non-empty leaf shards are written. There is no layout manifest: the layout is self-describing from which shard blobs exist. The model is 256 independently gated recursive subtrees, not one global tree — discovery, gating, and parallelism are anchored on the fixed 256 roots.

`MaxShardEntryCount = 1024` is the load-bearing number. It is chosen to minimize incremental-flush write-amplification: a daily archive touching ~200 roots writes ~2.2 MB at `T=1024` versus ~34 MB at `T=4096` (~15×). The decision deliberately optimizes the common incremental path over the once-per-machine cold rebuild path, where a larger threshold would have produced fewer, fatter shards.

Confidence: medium. The sharding shape and the parent-wins safety argument are implemented and exercised by routing tests; the exact `1024` threshold is grounded in a write-amplification model (rewrite-whole-shard × ~200 touched roots) rather than long-horizon production telemetry, so the precise value could shift if real multi-million-chunk workloads show a different touched-root distribution.

Before:

```text
chunk-index/<one or N fixed-width blobs>   # rewrite the whole touched blob each archive
```

After:

```text
chunk-index/aa                              # 256 fixed 2-hex roots
chunk-index/aaf  chunk-index/aa3 ...        # split 16-way only where a shard exceeds 1024 entries
                                            # layout self-describing from which blobs exist
                                            # routing: shallowest existing blob on the hash's prefix path wins
```

### Routing and split safety

`ChunkIndexRouter.ResolveTarget` walks down from a hash's 2-hex root and returns the *shallowest existing* shard blob on the hash's prefix path ("parent wins"); if no blob exists on the path it descends while any strictly-deeper blob shares the prefix (`BuildDescendantPrefixes` makes the "does a descendant exist?" test O(1)), and the terminal empty depth is where new entries are written. Parent-wins is what makes splitting crash-safe: `SplitShardAsync` uploads *all* non-empty leaf shards before deleting the parent or any stale blob in range, and skips the destructive delete scan entirely when the range was previously empty. A flush that crashes mid-split leaves the parent intact; because the snapshot for that run was never published, the parent still contains everything any published snapshot references, so parent-wins reads stay correct without a sentinel. The crashed run's rows stay `pending_flush = 1` and the retry re-resolves the parent and re-splits.

### Design point and failure mode

At the design point of ~1.3M chunks the layout settles at roughly ~4096 shards, which fits one 5000-blob Azure list page (≈900 shards of headroom) so layout discovery is one listing. Beyond ~1.3M the failure mode is gradual: the run-scoped listing simply fetches an additional page.

### Consequences and Tradeoffs

* Good, because incremental flush stays cheap: bounded shard size keeps steady-state write-amplification low (~2.2 MB/day at the modeled touched-root count).
* Good, because the layout adapts to repository size with no manifest and no operator tuning — depth grows only where data is dense.
* Good, because parent-wins routing plus upload-leaves-before-delete makes an interrupted split crash-safe with no sentinel or transaction across blobs.
* Good, because the realistic design point lists in a single Azure page, so layout discovery is one round-trip.
* Bad, because `1024` optimizes the incremental path at the cost of the cold full-rebuild path, which writes more, smaller shards.
* Bad, because split is one-directional within a normal flush — a shard only coarsens back during full `RepairAsync`, not when entries are deleted incrementally.
* Bad, because the self-describing layout makes routing depend on a consistent blob listing; a racing split (a blob listed at snapshot time but gone at download time) must reset the run-scoped listing and re-list once.

This decision deliberately does not address multi-machine cache coherence. Concurrent archives from two machines into the same repository can overwrite each other's shard rewrites (last-writer-wins); repair recovers, and etag-conditional writes are a possible future hardening. That coherence decision is out of scope here and belongs to ADR-0016.

### Confirmation

This decision is being followed when all of the following hold, verifiable in `Arius.Core.Shared.ChunkIndex`:

* `ChunkIndexService.MinShardPrefixLength == 2` and `ChunkIndexService.MaxShardEntryCount == 1024`.
* Routing resolves to the shallowest existing shard on a hash's prefix path (`ChunkIndexRouter.ResolveTarget` / `ShardTarget`), with the O(1) descendant test from `BuildDescendantPrefixes`.
* Over-threshold shards split 16-way by the next hex character into non-empty leaves only (`ChunkIndexRouter.PartitionIntoLeaves`, `GetChildPrefixes`).
* `SplitShardAsync` uploads all leaf shards before deleting the parent or stale blobs, and skips the delete scan when the range was empty.
* `RepairAsync` recomputes the layout from rebuilt entry counts and deletes every other `chunk-index/` blob (`ChunkIndexRepairResult`).

The `maxShardEntryCount` constructor parameter on `ChunkIndexService` is overridable so tests can force splits with few entries; routing and split partitioning are covered by the chunk-index router tests.

## Pros and Cons of the Options

### Single chunk-index blob

One blob holds every entry; every archive rewrites it.

* Good, because routing is trivial — there is one blob.
* Good, because there is no layout to discover or keep consistent.
* Bad, because write-amplification is catastrophic: a one-file daily archive rewrites the entire index.
* Bad, because the single blob grows unboundedly and a partial write risks the whole index.

### Fixed-width prefix sharding

A static set of `N`-hex-character shard blobs chosen up front.

* Good, because routing is a constant-time prefix slice with no walk.
* Good, because shards are independent and rewrite in parallel.
* Bad, because there is no width that suits both a tiny repository (too many empty round-trips) and a large one (oversized shards → high write-amplification).
* Bad, because growing past the chosen width is a global re-shard, not a local split.

### Dynamic-length prefix sharding

This is the chosen design: 2-hex roots that split 16-way per subtree at `MaxShardEntryCount`.

* Good, because shard size stays bounded, so incremental write-amplification stays low.
* Good, because depth adapts per subtree to data density with no manifest and no tuning.
* Good, because parent-wins routing makes splits crash-safe.
* Neutral, because the threshold trades cold-rebuild cost for incremental cost — a deliberate, documented bias toward the common path.
* Bad, because routing and split correctness depend on a consistent blob listing and careful upload-before-delete ordering.

## More Information

* Authoritative current description: [`design/core/shared/chunk-index.md`](../design/core/shared/chunk-index.md) (including the `MaxShardEntryCount = 1024` write-amplification motivation).
* Stage 1 — improve chunk-index scalability (foundation): `docs/history/openspec-archive/2026-05-29-improve-chunk-index-scalability/design.md`.
* Stage 2 — split chunk-index responsibilities: `docs/history/openspec-archive/2026-06-02-split-chunk-index-responsibilities/design.md`.
* Stage 3 — disk-backed chunk-index cache: `docs/history/openspec-archive/2026-06-08-disk-backed-chunk-index-cache/design.md`.
* Dynamic shard length plan: `docs/history/agentic-plans/2026-06-10-3-dynamic-shard-length/PLAN.md`.
* Latest scalability plan: `docs/history/agentic-plans/2026-06-17-improve-chunkindex-scalability/PLAN.md`.
