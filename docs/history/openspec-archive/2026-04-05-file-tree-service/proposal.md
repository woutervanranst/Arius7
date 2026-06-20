## Why

The `ls` and `restore` commands download every tree blob from Azure on each invocation — there is no local caching for filetree blobs in those code paths (`ListQueryHandler.WalkDirectoryAsync` and `RestoreCommandHandler.WalkTreeAsync` both call `_blobs.DownloadAsync` directly). The archive pipeline's `FileTreeBuilder` has its own ad-hoc disk cache (`File.Exists` + `File.WriteAllBytes` inlined in `EnsureUploadedAsync`), but this cache is not shared with other commands. Repeated `ls` or `restore` operations re-download hundreds of immutable, content-addressed blobs that never change.

A shared `FileTreeService` will eliminate redundant downloads across all commands and provide the foundation for fast-path tree existence checks during archive. This change also harmonizes the caching architecture: `ChunkIndexService` already encapsulates the three-tier chunk-index cache; `FileTreeService` follows the same `{Thing}Service` pattern for filetree blobs.

### Why a separate service (not combined with ChunkIndexService)

Chunk-index shards are **mutable** — they grow as new content hashes are recorded, and multiple machines can write overlapping shards. This requires L2 invalidation when another machine has written (detected via snapshot mismatch). Filetree blobs are **immutable** — they are content-addressed, so a cache entry is either absent or correct, never stale. This fundamental difference in invalidation semantics justifies separate services. Both follow the same naming pattern (`Shared/{Thing}/{Thing}Service.cs`) and the same cache directory convention (`~/.arius/{repo}/{thing}/`).

### Why disk-materialized prefetch of filetree blob names

During archive, `FileTreeBuilder.EnsureUploadedAsync` must check whether each tree blob already exists in remote storage. Today this is done with an individual `GetMetadataAsync` (HTTP HEAD) per tree blob — with hundreds of directory blobs, this is hundreds of round-trips. A single `ListAsync("filetrees/")` call retrieves all remote blob names in one request. On the slow path (snapshot mismatch), the results are materialized as empty files on disk at `~/.arius/{repo}/filetrees/{hash}` — existence checks then become `File.Exists`, the same check used on the fast path. This avoids holding an in-memory `HashSet` and the disk state persists across runs. On the **fast path** (this machine wrote the last snapshot), the local disk cache is already complete and trusted — no remote calls needed.

## What Changes

- **New `FileTreeService`**: A shared service in `Arius.Core/Shared/FileTree/` that reads and writes filetree blobs through the local disk cache at `~/.arius/{repo}/filetrees/`. Provides `ReadAsync(hash)` (cache-first download), `WriteAsync(hash, blob)` (write-through to cache + Azure), and `ExistsInRemote(hash)` (`File.Exists` check — reliable after `ValidateAsync` materializes remote state to disk on the slow path). Content-addressed blobs are immutable, so cache hits are always trusted.
- **Snapshot-based cache validation**: After each archive, an empty marker file named by the snapshot timestamp is written to `~/.arius/{repo}/snapshots/` (e.g., `~/.arius/{repo}/snapshots/2026-03-22T150000.000Z`), mirroring the blob container's `snapshots/` virtual directory layout. On archive start, the latest local marker (determined by enumerating and sorting the directory) is compared to the latest remote snapshot. Match = fast path (trust the disk cache for existence checks, no remote listing needed). Mismatch = slow path (call `ListAsync("filetrees/")` and materialize results as empty files on disk at `~/.arius/{repo}/filetrees/{hash}`, so that `ExistsInRemote` is always just `File.Exists`; also invalidate the chunk-index L2 disk cache since another machine may have written new shards).
- **Wire `ls` through cache**: `ListQueryHandler.WalkDirectoryAsync` currently calls `_blobs.DownloadAsync` directly for every tree blob (lines 102 and 258). This changes to use `FileTreeService.ReadAsync`, which checks the local disk cache first and writes through on miss.
- **Wire `restore` through cache**: `RestoreCommandHandler.WalkTreeAsync` similarly calls `_blobs.DownloadAsync` directly (line 569). This changes to use `FileTreeService.ReadAsync`.
- **Wire archive tree build through cache**: `FileTreeBuilder.EnsureUploadedAsync` has its own ad-hoc cache logic (disk check → HEAD → upload → disk write). This is replaced by `FileTreeService.WriteAsync` (write-through) and `FileTreeService.ExistsInRemote` (existence check using snapshot-aware fast/slow path).

## Capabilities

### New Capabilities
- `file-tree-service`: Shared filetree blob caching service with snapshot-based validation, providing read-through, write-through, and remote existence checking for all commands. Follows the same `{Thing}Service` naming pattern as `ChunkIndexService`.

### Modified Capabilities
- `list-query`: Tree blob reads change from direct `_blobs.DownloadAsync` to `FileTreeService.ReadAsync` (cache-first with disk write-through).
- `restore-pipeline`: Tree blob reads change from direct `_blobs.DownloadAsync` to `FileTreeService.ReadAsync` (cache-first with disk write-through).
- `archive-pipeline`: Tree build phase replaces ad-hoc disk cache in `EnsureUploadedAsync` with `FileTreeService.WriteAsync` and `FileTreeService.ExistsInRemote`. Snapshot check runs at pipeline start to determine fast/slow path.

## Impact

- **New service**: `FileTreeService` in `Arius.Core/Shared/FileTree/`, registered in DI. Depends on `IBlobContainerService` and `IEncryptionService` (same dependencies as existing `ChunkIndexService`).
- **Core layer** (`Arius.Core`): `ListQueryHandler`, `RestoreCommandHandler`, and `FileTreeBuilder` all change to depend on `FileTreeService` instead of calling blob storage or managing cache directly.
- **Cache directory**: Existing `~/.arius/{repo}/filetrees/` directory is reused for blob cache. After each archive, an empty marker file named by the snapshot timestamp is written to `~/.arius/{repo}/snapshots/` for cache validation.
- **Chunk-index L2 invalidation**: On snapshot mismatch, the chunk-index L2 disk cache is invalidated (files deleted) since another machine may have written new shards. This cross-service invalidation is triggered by `FileTreeService` (or a shared cache-validation component) and applied to the `ChunkIndexService` L2 directory.
- **No breaking changes**: All changes are internal; the CLI interface, blob storage layout, and wire formats are unchanged.
- **Tests**: New unit tests for `FileTreeService`:
  - Cache hit: `ReadAsync` returns cached blob without remote call.
  - Cache miss: `ReadAsync` downloads from remote, writes to disk cache, returns blob.
  - Write-through: `WriteAsync` uploads to remote and writes to disk cache.
  - Snapshot match (fast path): `ExistsInRemote` trusts disk cache (`File.Exists`), no `ListAsync` call.
  - Snapshot mismatch (slow path): `ValidateAsync` calls `ListAsync("filetrees/")`, creates empty marker files on disk, and `ExistsInRemote` returns correct results via `File.Exists`.
  - Snapshot mismatch triggers L2 chunk-index invalidation: On mismatch, chunk-index L2 disk cache files are deleted.
  - Prefetch completeness: After slow-path prefetch, all remote blob names have corresponding files on disk.
  - Updated integration tests for `ListQueryHandler`, `RestoreCommandHandler`, and `FileTreeBuilder` to verify they use `FileTreeService` instead of direct blob access.
