# Read queries

> **Code:** `src/Arius.Core/Features/{ChunkHydrationStatusQuery,ContainerNamesQuery,SnapshotsQuery,StatisticsQuery}/*.cs`  ·  **Terms:** [snapshot](../../../glossary.md#snapshot) · [storage tier hint](../../../glossary.md#storage-tier-hint)

## Purpose

Four thin Mediator read slices that back UI/host views. Each is a single query record + result + handler with no mutation: hydration status of files, Arius-container discovery in an account, the snapshot list, and aggregate repository statistics. They are grouped here because none carries enough intent to warrant its own page.

## How it works

Each slice is a `record` query implementing either `ICommand<T>` (materialized) or `IStreamQuery<T>` (progressive), dispatched through Mediator and serviced by one handler. The handlers read existing services — `IChunkIndexService`, `IChunkStorageService`, `ISnapshotService`, `IBlobServiceFactory` — and project their state into UI-shaped result records.

### ChunkHydrationStatusQuery

`ChunkHydrationStatusQuery(IReadOnlyList<RepositoryFileEntry>)` streams `ChunkHydrationStatusResult(RelativePath, ContentHash?, ChunkHydrationStatus)`. It is the **live rehydration truth**, in contrast to the [storage tier hint](../../../glossary.md#storage-tier-hint) recorded in the chunk index: the hint lets `ls` cheaply guess hydrated-vs-archived from the index, but only this query resolves the actual blob state.

The handler keeps only files in the repository with a content hash, batches one `IChunkIndexService.LookupAsync` to map content hash → chunk hash, then calls `IChunkStorageService.GetHydrationStatusAsync` per **distinct** chunk hash (cached in a `Dictionary<ChunkHash, ChunkHydrationStatus>` so many files sharing a chunk cost one call). A content hash missing from the index yields `ChunkHydrationStatus.Unknown`. Status values: `Unknown`, `Available`, `NeedsRehydration`, `RehydrationPending`.

### ContainerNamesQuery

`ContainerNamesQuery(AccountName, AccountKey?)` streams `string` container names for an Azure Blob account — Explorer's account picker uses this to discover Arius repositories. The handler resolves an `IBlobServiceFactory`, opens the account, and forwards `IBlobService.GetContainerNamesAsync`.

Arius-container detection lives in `AzureBlobService.GetContainerNamesAsync`: a container is an Arius repository iff it has at least one blob under the `snapshots/` prefix (`BlobPaths.SnapshotsPrefix`). The probe is one listing per container with `pageSizeHint: 1`, and the prefix blob itself is excluded so an empty `snapshots/` marker is not mistaken for a real repository. This filters out non-Arius containers (e.g. `$logs`, `$web`).

### SnapshotsQuery

`SnapshotsQuery()` is an `ICommand` returning a materialized `IReadOnlyList<SnapshotInfo>(Version, Timestamp, FileCount)` — the [snapshot](../../../glossary.md#snapshot) list for the time-travel picker. The set is materialized (not streamed) because there is one blob per snapshot and the whole set renders at once. The handler lists blob names oldest→newest via `ISnapshotService.ListBlobNamesAsync`, then resolves each manifest (disk-cache-first) for its timestamp and file count; unresolvable manifests are logged and skipped. `Version` is the snapshot blob filename, exactly what `ListQueryOptions.Version` / `RestoreOptions.Version` are `StartsWith`-matched against, so the UI can round-trip a version back into a list or restore.

### StatisticsQuery

`StatisticsQuery(Version? = null)` returns `RepositoryStatistics(Files, OriginalSize, StoredSize, UniqueChunks, StoredByTier)`. It joins two sources: the snapshot manifest supplies `Files` and `OriginalSize` (uncompressed totals); `IChunkIndexService.GetStatistics()` supplies stored size, distinct-chunk count, and the per-tier breakdown (`ChunkTierStatistic(Tier, UniqueChunks, StoredSize)`). No snapshot for the version ⇒ all-zero stats.

## Key invariants

- **Hydration status is keyed by distinct chunk, not file.** Multiple repository files dedup to the same chunk; their hydration status is identical and must be resolved once. Breaking the per-`ChunkHash` cache turns a list view into one blob call per file.
- **A container is an Arius repository iff it has a real blob under `snapshots/`.** The detection probe must stay a single bounded listing (`pageSizeHint: 1`) and must exclude the bare prefix marker — changing this either mislabels containers or makes account scans O(blobs).
- **`SnapshotInfo.Version` is the storage filename, not a display ordinal.** It is the value `Version` filters round-trip on; "v28"-style labels are UI ordinals derived from position, never persisted.
- **Statistics figures come from the local chunk-index cache, with no blob reads.** They reflect the cache's current coverage and finalise only once it has fully synchronised.

## Why this shape

These are deliberately thin vertical slices: query + result + handler, dispatched by Mediator, reading shared services and projecting UI-shaped records. The hydration query streams (status resolves per chunk, progressively); the snapshot and statistics queries materialize because their sets are small and rendered whole. The storage-tier-hint vs. live-truth split — cheap index hint for `ls`, authoritative blob probe only when asked — is documented on the [storage tier hint](../../../glossary.md#storage-tier-hint) term.

## Open seams / future

- `ChunkHydrationStatusQuery` issues per-distinct-chunk `GetHydrationStatusAsync` calls serially; a large archived selection could be parallelized if rehydration UX needs it.
- `StatisticsQuery` accuracy is bounded by chunk-index cache coverage; figures shift as the cache syncs and there is currently no "syncing / partial" signal in `RepositoryStatistics`.
- `ContainerNamesQuery` validates one container at a time; account scans with many non-Arius containers pay one listing each.
