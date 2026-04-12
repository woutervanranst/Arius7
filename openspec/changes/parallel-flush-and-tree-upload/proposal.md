## Why

The archive pipeline still ends with a serialized finalization tail. After uploads complete, it validates filetree cache state, flushes chunk-index shards one prefix at a time, sorts the manifest, builds filetrees while uploading each tree immediately, and only then creates the snapshot. The codebase has evolved since this idea was first proposed: `FileTreeBuilder` now owns the `FileTreeService.ValidateAsync` precondition for tree existence checks, and shard-prefix layout is a separate storage-contract question. We want to speed up finalization without coupling it to a repository layout change.

This change must remain grounded in Arius's operating constraints. Arius is a backup tool for important files, so durability and recoverability matter more than shaving off a little complexity. Repository scale can be large in bytes and large in file count: potentially terabyte-scale binary content with many thousands of small files. Blob storage is non-transactional across blobs, and local cache state can be stale or corrupt. Any finalization parallelism must preserve a clear commit point at snapshot creation and remain recoverable after partial uploads, partial shard flushes, and cache divergence.

## What Changes

- **Parallel chunk-index flush**: `ChunkIndexService.FlushAsync` keeps the current shard format but flushes touched prefixes in parallel with configurable concurrency. Each worker performs load -> merge -> serialize -> upload -> cache update for one prefix.
- **Bounded tree compute/upload pipeline**: `FileTreeBuilder.BuildAsync` performs one validation pass, computes directory tree blobs and hashes from the sorted manifest without further remote calls, writes upload-needed trees to bounded temporary spool files on disk, and lets parallel upload workers consume that spool. This preserves low memory usage even when the repository contains many directories.
- **Concurrent finalization work**: after manifest writing completes, archive finalization overlaps independent work instead of running it strictly back-to-back. Chunk-index flush runs concurrently with manifest sort, tree hash computation, and tree upload where dependencies allow; snapshot creation still waits for both finalization branches to finish.
- **Progress events for finalization**: add `ChunkIndexFlushProgressEvent(int ShardsCompleted, int TotalShards)` and `TreeUploadProgressEvent(int BlobsUploaded, int TotalBlobs)` so the CLI can show explicit progress during the longest end-of-pipeline phase.
- **Single validation owner**: the archive pipeline keeps one owner for filetree validation. If `FileTreeBuilder.BuildAsync` validates before using `ExistsInRemote`, `ArchiveCommandHandler` SHALL not perform a redundant pre-validation call.
- **Terminology cleanup note**: this change records that `manifest` currently refers to the temporary file-entry input to tree building, while `snapshot manifest` refers to the durable repository commit object. The naming overlap is confusing and should be normalized in domain language/docs as a follow-up consistency cleanup.

## Non-goals

- Changing chunk-index shard prefix length or blob naming format. That storage-layout work is tracked separately.

## Capabilities

### New Capabilities
- `parallel-flush-and-tree-upload`: Parallel chunk-index flush, parallel tree blob upload, overlapped end-of-pipeline execution, and explicit finalization progress events.

### Modified Capabilities
- `archive-pipeline`: End-of-pipeline changes from serialized validate -> flush -> sort -> build -> snapshot to an overlapped finalization flow with one validation owner and parallel progress reporting.

## Impact

- **Core layer** (`Arius.Core`): `ChunkIndexService.FlushAsync` and `FileTreeBuilder.BuildAsync` change significantly. `ArchiveCommandHandler` changes orchestration only where concurrency boundaries belong there.
- **No repository layout change**: `chunk-index/<prefix>` naming and current shard prefix length stay unchanged in this change.
- **New events**: `ChunkIndexFlushProgressEvent` and `TreeUploadProgressEvent` are added to archive events and consumed by CLI progress display.
- **Performance focus**: this change targets the end-of-pipeline tail dominated by sequential shard flush and sequential tree upload, while leaving shard-layout optimization for a follow-up change.
- **Durability constraints**: partial finalization work before snapshot creation is acceptable only as recoverable orphaned state; snapshot creation remains the repository commit point.
- **Tests**: add coverage for parallel flush correctness, bounded tree compute/upload behavior, concurrent finalization orchestration, single-owner validation, and progress event emission.