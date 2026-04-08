## Why

The archive pipeline's end-of-pipeline phases — chunk-index flush and filetree build/upload — are entirely sequential today. Chunk-index shards are flushed one at a time with `foreach`, tree blobs are uploaded one at a time via `EnsureUploadedAsync`, and the two phases run back-to-back. At 1M-chunk scale (~29K touched shards, hundreds of tree blobs), this takes over 4 hours. Parallelizing these independent I/O-bound operations and running the two phases concurrently can reduce this to seconds. Additionally, reducing the shard prefix from 4 hex chars to 3 consolidates 65,536 possible shards into 4,096, reducing per-shard overhead at scale. Neither phase emits progress events, leaving the user with no feedback during the longest part of an archive run.

## What Changes

- **Parallel chunk-index flush**: `ChunkIndexService.FlushAsync` changes from sequential `foreach` over modified shards to `Parallel.ForEachAsync` with configurable concurrency (default 32 workers). Each shard's load → merge → serialize → upload pipeline is independent.
- **3-char shard prefix**: **BREAKING** — The chunk-index shard key changes from 4 hex characters (65,536 shards) to 3 hex characters (4,096 shards). Shard blob paths change from `chunk-index/abcd` to `chunk-index/abc`. At 1M entries this yields ~244 entries/shard (~5 KB gzipped) — a good balance between shard count and shard size. This is a development-only breaking change; no migration is needed.
- **Separate tree compute from upload**: Tree construction splits into two phases: (1) sequential hash computation (CPU-bound, ~ms per blob), then (2) parallel upload of all tree blobs that need uploading, using `Parallel.ForEachAsync` with 32 workers.
- **Concurrent flush + tree build**: Chunk-index flush and tree build/upload run concurrently via `Task.WhenAll` instead of sequentially. These phases are independent — flush writes to `chunk-index/`, tree build writes to `filetrees/`.
- **Progress events for flush and tree upload**: Two new notification events: `ChunkIndexFlushProgressEvent(int ShardsCompleted, int TotalShards)` and `TreeUploadProgressEvent(int BlobsUploaded, int TotalBlobs)`. These enable the CLI to show two parallel progress lines during the end-of-pipeline phase.

## Capabilities

### New Capabilities
- `parallel-flush-and-tree-upload`: Parallel chunk-index flush, parallel tree blob upload, concurrent execution of both via `Task.WhenAll`, 3-char shard prefix, and progress events for both phases.

### Modified Capabilities
- `archive-pipeline`: End-of-pipeline changes from sequential flush → build → snapshot to concurrent (flush ∥ tree-build) → snapshot. New progress events emitted during flush and tree upload.
- `blob-storage`: Chunk-index shard blob path format changes from `chunk-index/<4-char-prefix>` to `chunk-index/<3-char-prefix>`.

## Impact

- **Core layer** (`Arius.Core`): `ChunkIndexService.FlushAsync` and `FileTreeBuilder.BuildAsync` (or `TreeService`) change significantly. `ArchiveCommandHandler` wires `Task.WhenAll` for the two phases.
- **Shard path format**: `chunk-index/` blob paths change from 4-char to 3-char prefixes. Existing L2 disk cache files become stale (acceptable — cache miss falls through to L3). Existing Azure shards from prior development archives become orphaned (acceptable — still in development, no production data).
- **New events**: `ChunkIndexFlushProgressEvent` and `TreeUploadProgressEvent` added to archive events. CLI display layer will consume these for parallel progress lines.
- **Performance**: At 1M-chunk scale, estimated end-of-pipeline time drops from ~265 minutes (sequential) to ~13 seconds (parallel, epoch-match fast path) or ~23 seconds (slow path with prefetch).
- **Tests**: New tests for parallel flush (verify all shards uploaded, concurrency correctness), parallel tree upload, concurrent execution, 3-char prefix shard paths, and progress event emission.
