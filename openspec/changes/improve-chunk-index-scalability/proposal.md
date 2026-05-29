## Why

The chunk index currently uses a fixed 4-hex shard prefix and flushes touched shards sequentially, which creates many tiny shard blobs for small archives while still allowing very large shards at high repository scale. Chunk-index cache invalidation is also hidden inside `FileTreeService.ValidateAsync`, and interrupted shard flushes rely on rerun behavior rather than an explicit repair path.

## What Changes

- Replace the hard-coded 4-hex shard prefix with one internal repository-wide prefix-length constant on `ChunkIndexService`, used by all shard prefix calculations.
- Parallelize chunk-index shard flush per touched prefix with bounded `Parallel.ForEachAsync` and an internal worker-count constant while preserving one worker per prefix.
- Add configurable lookup repair behavior: corrupt shards can be rebuilt automatically, restore can probe for chunks when an expected shard is missing, and normal archive misses do not trigger expensive repair scans.
- Make restore self-healing for incomplete chunk-index coverage: after normal lookup and missing-shard probing, unresolved snapshot content hashes trigger one full chunk-index repair and a retry before restore fails.
- Add an explicit full chunk-index repair API and command for maintenance and test setup, using one metadata-aware chunk listing and disk-backed shard rebuild before uploading repaired shards.
- Extend blob listing so callers can request metadata with listed blob names, enabling repair to avoid per-blob metadata round-trips.
- Move chunk-index cache invalidation out of `FileTreeService`; `FileTreeService.ValidateAsync` will still perform snapshot comparison and return `FileTreeValidationResult`, and `ArchiveCommandHandler` will invalidate chunk-index caches when that result reports a snapshot mismatch.
- Run chunk-index flush and filetree synchronization concurrently at the archive tail after cache validation and uploads complete, publishing a snapshot only after both succeed.
- Add interruption/recoverability coverage for a chunk-index flush stopped halfway across multiple shard prefixes.

## Capabilities

### New Capabilities

_(none)_

### Modified Capabilities

- `chunk-index-service`: Fixed prefix-length layout, bounded parallel flush, lookup repair modes, explicit full repair, and cache invalidation ownership.
- `archive-pipeline`: Explicit archive-tail coordination, concurrent index flush/filetree synchronization, storage-collision recovery after chunk-index misses, and recovery behavior for partial chunk-index flushes.
- `restore-pipeline`: Restore retries unresolved snapshot content after one full chunk-index repair before failing.
- `file-tree-service`: Filetree validation no longer invalidates chunk-index caches as a hidden side effect.
- `blob-storage`: Blob listing can optionally include metadata and content information for repair workflows.

## Impact

- `src/Arius.Core/Shared/ChunkIndex/`: `ChunkIndexService` prefix-length and flush-worker constants, lookup repair modes, parallel flush behavior, full repair API, and tests.
- `src/Arius.Core/Shared/Storage/IBlobContainerService.cs`: listing return shape and optional metadata flag.
- `src/Arius.AzureBlob/AzureBlobContainerService.cs`: Azure listing implementation with `BlobTraits.Metadata` when requested.
- `src/Arius.Core/Shared/FileTree/FileTreeService.cs`: remove chunk-index dependency and hidden invalidation.
- `src/Arius.Core/Features/ArchiveCommand/ArchiveCommandHandler.cs`: explicit cache coordination and parallel archive-tail finalization.
- `src/Arius.Core/Features/RestoreCommand/RestoreCommandHandler.cs`: missing-shard probe lookup mode, full repair fallback, retry unresolved lookups, and clear unresolved-entry errors.
- CLI command surface for explicit chunk-index repair.
- Integration/E2E tests for prefix-scoped repair, full repair, partial flush interruption, and archive-tail ordering.
