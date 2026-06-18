## Context

Today, filetree blob caching is fragmented across three code paths with inconsistent strategies:

1. **`FileTreeBuilder.EnsureUploadedAsync`** (archive): Ad-hoc disk cache at `~/.arius/{repo}/filetrees/`. Checks `File.Exists` → `GetMetadataAsync` (HEAD) → upload if needed → `File.WriteAllBytes`. The cache is write-through but only used for existence checks during archive, not for reads.
2. **`ListQueryHandler.WalkDirectoryAsync`** (ls): Zero caching. Calls `_blobs.DownloadAsync` directly for every tree blob on every invocation (lines 102 and 258).
3. **`RestoreCommandHandler.WalkTreeAsync`** (restore): Zero caching. Calls `_blobs.DownloadAsync` directly (line 569).

Meanwhile, `ChunkIndexService` already implements a clean three-tier cache (L1 in-memory LRU → L2 disk at `~/.arius/{repo}/chunk-index/` → L3 Azure) as a proper shared service. The local cache layout at `~/.arius/{repo}/` mirrors the blob container's virtual directory structure (`chunks/`, `filetrees/`, `snapshots/`, `chunk-index/`).

The archive pipeline runs these phases sequentially: `FlushAsync` → `BuildAsync` → `CreateAsync` (snapshot). The snapshot timestamp is known only after `CreateAsync` completes — this is when the snapshot marker file should be written.

## Goals / Non-Goals

**Goals:**
- Centralize all filetree blob reads and writes through a single `FileTreeService`, eliminating ad-hoc caching in `FileTreeBuilder` and uncached downloads in `ListQueryHandler`/`RestoreCommandHandler`.
- Provide a snapshot-based fast/slow path mechanism for existence checks during archive, avoiding per-blob HEAD requests when this machine wrote the last snapshot.
- Mirror the blob container's directory layout in the local cache (timestamp-named marker files live under `snapshots/`).
- Harmonize naming: `FileTreeService` follows the same `{Thing}Service` pattern as `ChunkIndexService`, lives in `Shared/FileTree/`.

**Non-Goals:**
- In-memory (L1) caching for tree blobs. Tree blobs are accessed infrequently (once per `ls`/`restore` invocation, once during archive tree build). L1 LRU adds complexity without benefit — disk reads are fast enough.
- Parallelizing tree uploads or chunk-index flush — that is the `parallel-flush-and-tree-upload` change.
- Changing the blob storage layout or wire format.
- Supporting concurrent archive operations from multiple machines on the same repo simultaneously (single-writer assumption is acceptable).

## Decisions

### Decision 1: Separate service, not combined with ChunkIndexService

**Choice**: `FileTreeService` as a separate class from `ChunkIndexService`.

**Rationale**: Chunk-index shards are **mutable** (they grow as new hashes are recorded, and multiple machines can write overlapping shards). This requires L2 invalidation on snapshot mismatch. Filetree blobs are **immutable** (content-addressed — a cached blob is either absent or correct, never stale). This fundamental difference in invalidation semantics means the services have different cache strategies:
- `ChunkIndexService`: L1 LRU → L2 disk → L3 Azure, with L2 invalidation on mismatch.
- `FileTreeService`: Disk → Azure, with no invalidation ever (immutable blobs). The only "invalidation" is the existence-check prefetch on mismatch, which is conceptually different.

**Alternatives considered**: A shared `CacheService<T>` base class was considered but rejected — the two services share almost no logic beyond "check disk, fall back to Azure." The prefetch/existence-check mechanism is unique to filetree blobs.

### Decision 2: Snapshot validation via timestamp-named marker files in `~/.arius/{repo}/snapshots/`

**Choice**: After each archive, write an empty marker file named by the snapshot timestamp to `~/.arius/{repo}/snapshots/` (e.g., `2026-03-22T150000.000Z`). To determine the latest local snapshot, enumerate the directory and sort by parsed timestamp — the same approach `SnapshotService.ListBlobNamesAsync` uses for remote snapshots.

**Rationale**: The `snapshots/` directory mirrors the blob container's virtual directory layout, keeping the local cache structure intuitive (`ls ~/.arius/{repo}/` shows `chunk-index/`, `filetrees/`, `snapshots/` — same as the container). Marker files named by timestamp give a natural local history of all snapshots this machine has written, and inferring the latest requires only enumerating and sorting — trivially cheap for a directory with typically < 100 entries. The `SnapshotService.TimestampFormat` (`"yyyy-MM-ddTHHmmss.fffZ"`) sorts lexicographically, so a simple string sort suffices.

On archive start:
1. Enumerate `snapshots/` directory → sort → latest local timestamp (or null if empty).
2. Call `ListAsync("snapshots/")` to get the latest remote snapshot name → remote timestamp. (This is a lightweight call — typically < 100 snapshots.)
3. Compare: if local == remote, **fast path**. If mismatch (or no local markers), **slow path**.

**Alternatives considered**: Using a single `.last` file containing the latest timestamp. Rejected — timestamp-named marker files mirror the remote layout more faithfully and provide a natural local history without additional complexity (the timestamp format already sorts lexicographically).

### Decision 3: Disk-materialized prefetch via ListAsync on slow path

**Choice**: On snapshot mismatch, call `ListAsync("filetrees/")` once and create an empty file on disk at `~/.arius/{repo}/filetrees/{hash}` for each remote blob that isn't already cached locally. Existence checks then become `File.Exists` — the same check used on the fast path.

**Rationale**: During archive, `EnsureUploadedAsync` checks existence per tree blob. Today this is done via `GetMetadataAsync` (HTTP HEAD) — one round-trip per blob. With hundreds of directory blobs, this is hundreds of sequential HTTP calls. A single `ListAsync("filetrees/")` retrieves all names in one paginated call (~seconds even at scale), and materializing the results as empty files on disk means `ExistsInRemote` is always just `File.Exists` regardless of fast/slow path.

This approach has several advantages over an in-memory `HashSet`:
- **Zero in-memory overhead** — no hash set allocation regardless of blob count. Scales to arbitrarily large repositories.
- **Unified code path** — `ExistsInRemote` is `File.Exists` on both fast and slow paths. No fast/slow branching needed.
- **Persistent** — the disk state converges toward the remote state. Subsequent runs benefit even if they take the slow path again.
- **Self-warming** — empty marker files get "filled" with actual plaintext content as `ReadAsync`/`WriteAsync` are called, so the cache naturally warms up.

The prefetch only runs on the **slow path** (snapshot mismatch). On the **fast path** (this machine wrote the last snapshot), the disk cache is already complete and trusted.

**Alternatives considered**:
- *In-memory `HashSet<byte[]>`*: Rejected — linear memory growth with repository size, and the prefetch results are discarded at end of process. Disk-materialized prefetch is more efficient and persistent.
- *Lazy per-blob HEAD requests*: Rejected — hundreds of individual HEAD requests dominate the tree build phase.

### Decision 4: Snapshot mismatch triggers chunk-index L2 invalidation

**Choice**: When a snapshot mismatch is detected (another machine wrote the last snapshot), delete all files in the chunk-index L2 directory (`~/.arius/{repo}/chunk-index/`).

**Rationale**: If another machine wrote the last snapshot, it may have modified chunk-index shards. The local L2 disk cache may contain stale shard data. Since shards are mutable, we cannot trust L2 on mismatch. Deleting L2 forces `ChunkIndexService` to re-download from L3 on next access.

This is a **cross-service concern**: `FileTreeService` (or the validation logic) detects the mismatch, but the invalidation targets `ChunkIndexService`'s L2 directory. Implementation options:
- **(a)** `FileTreeService` directly deletes the L2 directory (it knows the path via `ChunkIndexService.GetL2Directory`).
- **(b)** Extract a `CacheValidationService` that owns the snapshot comparison and triggers invalidation on both services.
- **(c)** `FileTreeService` exposes an event/callback, `ChunkIndexService` subscribes.

**Selected: (a)** — Direct deletion is simplest. Both services already receive `accountName` and `containerName` in their constructors, so `FileTreeService` can call `ChunkIndexService.GetL2Directory(accountName, containerName)` and delete the directory. No new abstractions needed. If more cross-service invalidation emerges in the future, we can extract a shared component then.

### Decision 5: FileTreeService API surface

**Choice**: Four public methods:

- `ReadAsync(string hash, CancellationToken)` → `Task<FileTreeBlob>`: Check disk cache → download from Azure → deserialize → write to disk cache → return. Used by `ls`, `restore`, and `archive` tree build (for reading existing blobs).
- `WriteAsync(string hash, FileTreeBlob, CancellationToken)` → `Task`: Serialize → upload to Azure → write plaintext to disk cache. Used by `archive` tree build when creating new blobs.
- `ExistsInRemote(string hash)` → `bool`: Checks disk cache via `File.Exists`. After `ValidateAsync` has run on the slow path, empty marker files have been materialized for all remote blobs, so `File.Exists` is reliable in both paths. Must be called after `ValidateAsync` has run.
- `ValidateAsync(CancellationToken)` → `Task`: Enumerates local snapshot markers, compares with latest remote snapshot, materializes empty disk files for remote blobs + L2 invalidation on mismatch. Called once at archive pipeline start.

**Rationale**: `ReadAsync` and `WriteAsync` handle serialization/deserialization internally (including encryption), matching how `ChunkIndexService` encapsulates shard serialization. `ExistsInRemote` is always `File.Exists` — the simplicity comes from materializing the remote listing to disk on the slow path. `ValidateAsync` is explicit rather than lazy to make the snapshot comparison deterministic and testable.

### Decision 6: DI registration and lifecycle

**Choice**: `FileTreeService` is registered as a singleton (one per `AddArius` call), matching `ChunkIndexService`'s lifecycle.

**Rationale**: The service holds no significant in-memory state — existence checks use the disk, and the validated flag ensures `ValidateAsync` is called before `ExistsInRemote`. Constructor takes `IBlobContainerService`, `IEncryptionService`, `string accountName`, `string containerName` — same pattern as `ChunkIndexService`.

## Risks / Trade-offs

**[Risk] `ListAsync("filetrees/")` is slow for very large repositories** → At 1M chunks, the number of distinct tree blobs is bounded by the number of directories, not files. Even a deeply nested archive with thousands of directories will have < 10K tree blobs. `ListAsync` returns blob names (small strings), so even 10K results complete in seconds. Acceptable.

**[Risk] Disk-materialized prefetch creates many empty files** → On the slow path, one empty file per remote tree blob is created. At 10K tree blobs this is 10K files — trivial for modern filesystems. The files are tiny (0 bytes) and live in a single flat directory. They get filled with real content as `ReadAsync`/`WriteAsync` are called.

**[Risk] `ListAsync("snapshots/")` adds latency on every archive start** → Snapshot count grows by 1 per archive run. Even after hundreds of runs, the list is small (< 1KB response). Latency is ~100ms. Acceptable — this is the price of detecting multi-machine writes.

**[Risk] Cross-service L2 invalidation is a coupling** → `FileTreeService` knows about `ChunkIndexService.GetL2Directory`. This is pragmatic coupling for a real operational requirement. If more cross-service invalidation patterns emerge, we can extract a shared `CacheValidationService`. For now, YAGNI.

**[Trade-off] No L1 in-memory cache for tree blobs** → Tree blobs are accessed once per directory during tree traversal. An L1 LRU would add complexity (eviction, memory budget) with minimal benefit since each blob is typically read exactly once per invocation. Disk I/O for cached blobs is ~1ms.

**[Trade-off] Marker files are not atomic** → A crash between snapshot creation and marker file write could leave the local `snapshots/` directory missing the latest entry. The consequence is a spurious slow path on the next archive — a performance penalty, not a correctness issue. The slow path still produces correct results.
