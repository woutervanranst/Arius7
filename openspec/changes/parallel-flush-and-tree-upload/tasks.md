## 1. Validation Ownership And Finalization Shape

- [x] 1.1 Remove the redundant `ArchiveCommandHandler` call to `FileTreeService.ValidateAsync()` so `FileTreeBuilder` remains the single validation owner for tree existence checks
- [x] 1.2 Refactor archive end-of-pipeline orchestration so chunk-index flush overlaps with the manifest-sort/tree branch while `SnapshotService.CreateAsync()` still waits for both branches to finish
- [x] 1.3 Add or update tests that assert snapshot creation does not occur until both flush and tree upload work complete

## 2. Parallel Chunk-Index Flush

- [x] 2.1 Refactor `ChunkIndexService.FlushAsync()` to snapshot pending entries into per-prefix groups before worker launch
- [x] 2.2 Flush touched shard prefixes in parallel with bounded concurrency, preserving the existing per-prefix load -> merge -> serialize -> upload -> L2 save -> L1 promote flow
- [x] 2.3 Emit `ChunkIndexFlushProgressEvent(int ShardsCompleted, int TotalShards)` during parallel flush
- [x] 2.4 Add tests for parallel flush correctness, including all touched prefixes uploaded, L2/L1 cache updates preserved, and duplicate/overlapping content hashes merged correctly under concurrency

## 3. Bounded Tree Compute/Upload Pipeline

- [x] 3.1 Refactor `FileTreeBuilder.BuildAsync()` so directory hash computation remains local after validation and does not depend on remote upload completion
- [x] 3.2 Introduce a bounded producer/consumer pipeline for upload-needed tree blobs: compute writes temporary spool files outside the final filetree cache directory, upload workers consume descriptors from a bounded channel
- [x] 3.3 Publish plaintext tree bytes into the final filetree cache path only after upload succeeds, so crash recovery can distinguish tentative local spool state from confirmed remote existence
- [x] 3.4 Emit `TreeUploadProgressEvent(int BlobsUploaded, int TotalBlobs)` during tree upload
- [x] 3.5 Add tests for bounded disk-spooled tree upload behavior, including deduplicated trees skipped without upload, successful uploads populating the final cache, and interrupted runs not leaving false-positive final cache entries

## 4. CLI Finalization Progress

- [x] 4.1 Add CLI notification handlers and progress-state support for chunk-index flush progress and tree upload progress
- [x] 4.2 Update archive progress rendering so finalization shows explicit concurrent progress instead of appearing stalled after chunk upload completes
- [x] 4.3 Add CLI tests that verify finalization progress remains correct when progress events arrive out of order from parallel workers

## 5. Verification

- [x] 5.1 Run unit and integration tests covering archive finalization, filetree caching, chunk-index flush, and archive CLI progress
- [x] 5.2 Add a regression test documenting that snapshot creation remains the only repository commit point: partial flush/tree-upload work before snapshot must not be treated as a completed archive state
- [x] 5.3 Review domain-language/docs touchpoints for the temporary archive manifest versus durable snapshot naming overlap and capture any follow-up cleanup work without renaming storage artifacts in this change
