# Read queries

> **Code:** `src/Arius.Core/Features/{ChunkHydrationStatusQuery,ContainerNamesQuery,SnapshotsListQuery,SnapshotDiffQuery,StatisticsQuery}/*.cs`  ·  **Terms:** [snapshot](../../../glossary.md#snapshot) · [storage tier hint](../../../glossary.md#storage-tier-hint)

## Purpose

Four thin Mediator read slices that back UI/host views. Each is a single query record + result + handler with no mutation: hydration status of files, Arius-container discovery in an account, the snapshot list, and aggregate repository statistics. They are grouped here because none carries enough intent to warrant its own page.

## How it works

Each slice is a `record` query implementing `ICommand<T>` / `IQuery<T>` (materialized) or `IStreamQuery<T>` (progressive), dispatched through Mediator and serviced by one handler. The handlers read existing services — `IChunkIndexService`, `IChunkStorageService`, `ISnapshotService`, `IBlobServiceFactory`, `IStorageCostEstimator` — and project their state into UI-shaped result records.

### ChunkHydrationStatusQuery

`ChunkHydrationStatusQuery(IReadOnlyList<RepositoryFileEntry>)` streams `ChunkHydrationStatusResult(RelativePath, ContentHash?, ChunkHydrationStatus)`. It is the **live rehydration truth**, in contrast to the [storage tier hint](../../../glossary.md#storage-tier-hint) recorded in the chunk index: the hint lets `ls` cheaply guess hydrated-vs-archived from the index, but only this query resolves the actual blob state.

The handler keeps only files in the repository with a content hash, batches one `IChunkIndexService.LookupAsync` to map content hash → chunk hash, then calls `IChunkStorageService.GetHydrationStatusAsync` per **distinct** chunk hash (cached in a `Dictionary<ChunkHash, ChunkHydrationStatus>` so many files sharing a chunk cost one call). A content hash missing from the index yields `ChunkHydrationStatus.Unknown`. Status values: `Unknown`, `Available`, `NeedsRehydration`, `RehydrationPending`.

### ContainerNamesQuery

`ContainerNamesQuery(AccountName, AccountKey?)` streams `string` container names for an Azure Blob account — Explorer's account picker uses this to discover Arius repositories. The handler resolves an `IBlobServiceFactory`, opens the account, and forwards `IBlobService.GetContainerNamesAsync`.

Arius-container detection lives in `AzureBlobService.GetContainerNamesAsync`: a container is an Arius repository iff it has at least one blob under the `snapshots/` prefix (`BlobPaths.SnapshotsPrefix`). The probe is one listing per container with `pageSizeHint: 1`, and the prefix blob itself is excluded so an empty `snapshots/` marker is not mistaken for a real repository. This filters out non-Arius containers (e.g. `$logs`, `$web`).

### SnapshotsListQuery

`SnapshotsListQuery()` is an `IStreamQuery<SnapshotInfo>` streaming `SnapshotInfo(Version, Timestamp, FileCount)` — the [snapshot](../../../glossary.md#snapshot) list for the time-travel picker, the CLI `snapshot list`, and `arius snapshot diff` argument resolution. It streams (one blob per snapshot, resolved oldest→newest) so callers render rows as each manifest resolves. The handler lists blob names oldest→newest via `ISnapshotService.ListBlobNamesAsync`, then resolves each manifest (disk-cache-first) for its timestamp and file count; unresolvable manifests are logged and skipped.

### SnapshotDiffQuery

`SnapshotDiffQuery(VersionA, VersionB)` is an `IStreamQuery<SnapshotDiffEntry>` reporting what changed between two snapshots. It resolves both manifests (`ISnapshotService.ResolveAsync`), warns when their `AriusVersion` differs (the cross-platform line-ending hash boundary makes identical content hash differently), then BFS-walks both root [filetrees](../../../glossary.md#filetree) in lockstep: child directories with an equal `FileTreeHash` are **pruned** (never read), so work is `O(changed nodes)`. Each remaining leaf is classified into exactly one `ChangeType` — `Added` (path only in B), `Removed` (path only in A), `Modified` (same path, different `ContentHash`), `TimestampChanged` (same path + same `ContentHash`, different timestamps); identical leaves are not emitted. The classification is MECE: `ChangeType` is a plain enum, not `[Flags]`. Rename detection and net-new-content reporting are deliberately out of scope (see the design spec). Each `SnapshotDiffEntry` carries the `FileEntry` from snapshot A (`Before`) and B (`After`); `Added`⇒`Before` null, `Removed`⇒`After` null.

### StatisticsQuery

`StatisticsQuery(Version? = null, EnsureFullCoverage = false)` is an `IQuery<RepositoryStatistics>` returning `RepositoryStatistics(Files, OriginalSize, DeduplicatedSize, StoredSize, UniqueChunks, TotalStorageCostPerMonth, StoredByTier)` (costs in EUR). It joins three sources across **two scopes** plus a cost layer:

- **Per-snapshot** (from the resolved snapshot manifest): `Files` and `OriginalSize` — the logical size of *this* snapshot, i.e. the sum of original (uncompressed) file sizes counting duplicates once per file (the size you would restore).
- **Repository-wide** (from `IChunkIndexService`, across all snapshots): a single `GetStatistics()` call returns `ChunkIndexStatistics(DeduplicatedOriginalSize, ByTier)`. `DeduplicatedSize` ← `DeduplicatedOriginalSize` (sum of original sizes over distinct content, *before* compression); `StoredSize` and `UniqueChunks` come from `ByTier` (the deduplicated *and* compressed cloud footprint).
- **Cost** (from `IStorageCostEstimator.EstimateStorageCost(ByTier)`): the handler prices each tier (in EUR) against the container's own [region](../../../glossary.md#region) — resolved by the estimator, not passed in — producing `TotalStorageCostPerMonth` and the per-tier `StoredByTier` (`TierStorageCost(Tier, UniqueChunks, StoredSize, CostPerMonth)`). The estimate's resolved region picks the rates but is **not** surfaced on `RepositoryStatistics`. Pricing is the provider adapter's concern — see [cost estimation](../../core/shared/cost.md) ([ADR-0020](../../../decisions/adr-0020-provider-agnostic-cost-estimation.md)).

The repository-wide figures are read straight from the local chunk-index cache, so by default they reflect only the coverage that browsing/lookups happened to populate — accurate only once the cache has fully synced. When `EnsureFullCoverage` is set, the handler first calls `IChunkIndexService.EnsureFullCoverageAsync` (Stage 2), which downloads (or etag-revalidates) every remote shard into the cache, so the figures are **complete** rather than partial. That sweep touches blob storage and is the slow path; callers that need honest repository-wide totals (the web Statistics screen) lazy-load behind this flag, and the host memoizes the result so the sweep is paid once per snapshot generation (see [web host](../../hosts/web.md#statistics-cache)).

The three sizes form a logical→physical chain: `OriginalSize` (logical, with duplicates) ≥ `DeduplicatedSize` (unique, uncompressed) ≥ `StoredSize` (unique, compressed). No snapshot for the version ⇒ all-zero stats.

## Key invariants

- **Hydration status is keyed by distinct chunk, not file.** Multiple repository files dedup to the same chunk; their hydration status is identical and must be resolved once. Breaking the per-`ChunkHash` cache turns a list view into one blob call per file.
- **A container is an Arius repository iff it has a real blob under `snapshots/`.** The detection probe must stay a single bounded listing (`pageSizeHint: 1`) and must exclude the bare prefix marker — changing this either mislabels containers or makes account scans O(blobs).
- **`SnapshotInfo.Version` is the storage filename, not a display ordinal.** It is the value `Version` filters round-trip on; "v28"-style labels are UI ordinals derived from position, never persisted.
- **Statistics mix per-snapshot and repository-wide scopes.** `Files` and `OriginalSize` are scoped to the resolved snapshot; `DeduplicatedSize`, `StoredSize`, `UniqueChunks`, and `StoredByTier` are repository-wide (all snapshots), read from the chunk index. The two scopes must be labelled distinctly in any UI so a snapshot's logical size is not read as the repository's physical footprint.
- **Statistics chunk-index figures come from the local chunk-index cache.** On the default path no blob is read, so the repository-wide figures reflect only the cache's current coverage and finalise only once it has fully synchronised; `EnsureFullCoverage` trades that speed for completeness by first sweeping every remote shard into the cache (`EnsureFullCoverageAsync`) — the one path on which this query touches storage.
- **Cost is region-priced and provider-owned.** The per-tier cost and total (in EUR) come from the provider's catalog, priced against the container's own [region](../../../glossary.md#region) (resolved by the estimator; unset → provider default), never computed in Core ([cost estimation](../../core/shared/cost.md)). The region lives in container metadata and is set out-of-band, so a memoized `StatisticsDto` is **not** automatically re-priced when the region changes — see the [web host](../../hosts/web.md#statistics-cache) cache seam.

## Why this shape

These are deliberately thin vertical slices: query + result + handler, dispatched by Mediator, reading shared services and projecting UI-shaped records. The hydration, snapshot list, and diff queries stream (status or entries resolve progressively); the statistics query materializes because its set is small and rendered whole. The storage-tier-hint vs. live-truth split — cheap index hint for `ls`, authoritative blob probe only when asked — is documented on the [storage tier hint](../../../glossary.md#storage-tier-hint) term.

## Open seams / future

- `ChunkHydrationStatusQuery` issues per-distinct-chunk `GetHydrationStatusAsync` calls serially; a large archived selection could be parallelized if rehydration UX needs it.
- `StatisticsQuery`'s default-path accuracy is bounded by chunk-index cache coverage; figures shift as the cache syncs and there is no "syncing / partial" signal in `RepositoryStatistics`. `EnsureFullCoverage` removes the bound at the cost of a full-index download — there is no middle ground (incremental "fill the gaps as you go") yet.
- `ContainerNamesQuery` validates one container at a time; account scans with many non-Arius containers pay one listing each.
