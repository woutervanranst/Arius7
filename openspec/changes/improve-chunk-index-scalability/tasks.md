## 1. Storage Listing and Metadata Foundations

- [ ] 1.1 Add a blob-list item model containing the blob name, metadata, and available content information needed by repair workflows.
- [ ] 1.2 Change `IBlobContainerService.ListAsync` to return blob-list items and accept `includeMetadata = false` while preserving name-only caller behavior.
- [ ] 1.3 Update existing `ListAsync` callers to read blob names from the returned item model without requiring metadata when `includeMetadata` is false.
- [ ] 1.4 Implement metadata-aware Azure listing with `BlobTraits.Metadata` when `includeMetadata` is true.
- [ ] 1.5 Update storage fakes and storage tests for name-only listing, metadata-aware listing, and listed content information.
- [ ] 1.6 Add the thin chunk parent tar chunk hash metadata key `parent_chunk_hash` to shared blob constants.

## 2. Chunk Index Layout and Lookup Failures

- [ ] 2.1 Add `ContentHash.Prefix(int length)` with validation and tests for supported and invalid prefix lengths.
- [ ] 2.2 Add internal `ChunkIndexService.ShardPrefixLength` and route `Shard.PrefixOf`, local L2 shard paths, remote shard paths, lookup, flush, and repair through it.
- [ ] 2.3 Remove remaining hard-coded 4-hex chunk-index prefix assumptions from production code and tests.
- [ ] 2.4 Add `ChunkIndexCorruptException` and `ChunkIndexRepairIncompleteException` with user-facing messages that instruct users to run the explicit repair command.
- [ ] 2.5 Change lookup so missing remote shard blobs are treated as empty shards without listing `chunks/` or rewriting chunk-index shards.
- [ ] 2.6 Change lookup so corrupt remote shard blobs fail with `ChunkIndexCorruptException` instead of attempting automatic repair.
- [ ] 2.7 Keep local L2 corruption recoverable by deleting the corrupt local shard and reloading from remote storage when possible.
- [ ] 2.8 Ensure valid shards with missing entries return misses and do not trigger prefix-scoped or full repair.

## 3. Chunk Index Repair State and Flush

- [ ] 3.1 Add an internal repair in-progress marker path outside the purgeable chunk-index shard-cache directory.
- [ ] 3.2 Guard normal lookup, entry recording, and pending-entry flush so they throw `ChunkIndexRepairIncompleteException` when the repair marker exists.
- [ ] 3.3 Allow `ChunkIndexService` construction and explicit full repair to proceed when the repair marker already exists.
- [ ] 3.4 Update chunk-index cache invalidation to delete shard-cache files and invalidate L1 without deleting the repair marker.
- [ ] 3.5 Add internal `ChunkIndexService.FlushWorkers` and process touched shard prefixes with bounded `Parallel.ForEachAsync`.
- [ ] 3.6 Ensure each flush worker owns one shard prefix end-to-end: load, merge, upload, save L2, and promote L1.
- [ ] 3.7 Ensure chunk-index flush failures propagate so archive fails and does not publish a snapshot after a partial parallel flush.

## 4. Chunk Storage and Archive Uploads

- [ ] 4.1 Update `ChunkStorageService.UploadThinAsync` to upload an empty body with `arius_type`, `parent_chunk_hash`, `original_size`, and `compressed_size` metadata in one call.
- [ ] 4.2 Keep committed thin-chunk collision recovery inside `UploadThinAsync` and retry incomplete existing thin chunks.
- [ ] 4.3 Update archive tar-bundle handling to pass the parent tar chunk hash to `UploadThinAsync`.
- [ ] 4.4 Record thin chunk-index entries as content hash to parent tar chunk hash with original and compressed size metadata.
- [ ] 4.5 Add large-chunk collision recovery after a chunk-index miss by reading complete existing chunk metadata and recording the missing chunk-index entry.
- [ ] 4.6 Ensure archive dedup lookup failures for corrupt shards or interrupted repair state fail with repair instructions and do not run automatic repair.
- [ ] 4.7 Preserve the single-threaded dedup in-flight set behavior so duplicate content in one archive run uploads only once.

## 5. Full Chunk Index Repair

- [ ] 5.1 Add the explicit full repair API on `ChunkIndexService` with cancellation support and repair result counts for logs and CLI output.
- [ ] 5.2 At repair start, invalidate L1, create the repair marker, purge local L2 shard-cache files, and tolerate pre-existing marker reruns.
- [ ] 5.3 Rebuild from one `ListAsync("chunks/", includeMetadata: true, ...)` scan of committed chunk blobs.
- [ ] 5.4 Reconstruct large chunk entries from `arius_type: large` blob names and metadata without reading existing chunk-index shards.
- [ ] 5.5 Reconstruct thin chunk entries from `arius_type: thin` metadata without downloading thin chunk bodies.
- [ ] 5.6 Fail full repair clearly when committed thin chunk metadata is missing or has invalid `parent_chunk_hash`, `original_size`, or `compressed_size`.
- [ ] 5.7 Ignore tar chunks directly and ignore blobs without recognized committed `arius_type` metadata.
- [ ] 5.8 Merge reconstructed entries into local L2 shard files by configured shard prefix without materializing the full index in memory.
- [ ] 5.9 Upload every rebuilt non-empty shard to `chunk-index/<prefix>` and do not upload empty shard blobs.
- [ ] 5.10 List existing `chunk-index/` blobs and delete remote shard blobs not present in the rebuilt prefix set.
- [ ] 5.11 Clear the repair marker only after rebuilt shards are uploaded and stale remote shards are deleted.
- [ ] 5.12 Ensure full repair is idempotent, can rerun after interruptions, and never creates or updates snapshots.

## 6. CLI, Cache Coordination, and Archive Tail

- [ ] 6.1 Add the explicit chunk-index repair CLI command and wire it through DI to the `ChunkIndexService` full repair API.
- [ ] 6.2 Add repair command console output and failure messages that identify when users should rerun repair.
- [ ] 6.3 Update `README.md` with the repair command and when to use it.
- [ ] 6.4 Change `FileTreeService.ValidateAsync` to return `FileTreeValidationResult` with `SnapshotMismatch` and remove any `ChunkIndexService` dependency or invalidation side effect.
- [ ] 6.5 Update `ArchiveCommandHandler` to invalidate chunk-index caches when filetree validation reports `SnapshotMismatch`, before tree existence checks or chunk-index flush.
- [ ] 6.6 Run `ChunkIndexService.FlushAsync` and `FileTreeBuilder.SynchronizeAsync` concurrently after uploads and cache coordination complete.
- [ ] 6.7 Create the snapshot only after chunk-index flush and filetree synchronization both complete successfully.

## 7. Restore, List, and Audit Logging

- [ ] 7.1 Update restore to classify missing-shard lookup misses for snapshot content as unresolved content hashes.
- [ ] 7.2 Make restore fail with a clear repair instruction when any snapshot-referenced content hashes remain unresolved after lookup.
- [ ] 7.3 Make restore propagate corrupt-index and interrupted-repair errors with the explicit repair instruction and without automatic repair.
- [ ] 7.4 Ensure restore still groups entries by resolved chunk hash so tar chunks are downloaded once.
- [ ] 7.5 Update `ls` size lookup to keep missing content hashes as `OriginalSize = null` and fail on corrupt-index or interrupted-repair errors with repair instructions.
- [ ] 7.6 Update archive, restore, `ls`, and repair logging to follow the ADR-0007 lifecycle, phase, and detail taxonomy.
- [ ] 7.7 Remove redundant completion logs that only restate phase markers, especially around concurrent archive-tail work.
- [ ] 7.8 Add useful `[index]`, `[tar]`, `[chunk]`, `[restore]`, `[repair]`, and `[phase]` payloads without logging full hashes.

## 8. Tests and Verification

- [ ] 8.1 Add unit tests for storage metadata-aware listing, storage fakes, and thin chunk parent metadata exposure.
- [ ] 8.2 Add chunk-index unit tests for prefix layout, missing and corrupt shard lookup, local L2 corrupt reload, valid missing entries, repair marker guards, invalidation, and parallel flush.
- [ ] 8.3 Add full-repair tests for large and thin reconstruction, invalid thin metadata failure, single metadata-aware chunk listing, stale shard deletion, marker lifecycle, idempotent rerun, and no snapshot publication.
- [ ] 8.4 Add archive pipeline tests for thin chunk metadata, existing large chunk collision recovery, snapshot mismatch invalidation ownership, concurrent archive tail, and partial flush failure without snapshot publication.
- [ ] 8.5 Add restore and list tests for unresolved content hashes, corrupt-index failures, interrupted-repair failures, and missing-size null behavior.
- [ ] 8.6 Add integration or E2E coverage for full repair, corrupt-index failure, and chunk-index flush interruption across multiple shard prefixes.
- [ ] 8.7 Measure coverage for `src/Arius.Core/Shared/ChunkIndex/` and raise it to at least 90%.
- [ ] 8.8 Run the relevant unit, integration, and E2E test projects for the changed archive, restore, list, storage, and CLI behavior.
