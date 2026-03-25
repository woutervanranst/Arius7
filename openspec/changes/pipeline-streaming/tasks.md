## 1. Stream Wrappers

- [ ] 1.1 Create `ProgressStream` class: read-mode wrapper reporting `IProgress<long>` on cumulative bytes read
- [ ] 1.2 Create `CountingStream` class: write-mode wrapper tracking `BytesWritten` property
- [ ] 1.3 Unit test `ProgressStream` (progress callbacks, zero-length file, total matches file length)
- [ ] 1.4 Unit test `CountingStream` (BytesWritten accuracy, readable after dispose)

## 2. Blob Storage Abstraction

- [ ] 2.1 Add `OpenWriteAsync(string path, string contentType, AccessTier tier)` returning `Stream` to `IBlobStorageService`
- [ ] 2.2 Implement `OpenWriteAsync` in `AzureBlobStorageService` using `BlockBlobClient.OpenWriteAsync()`
- [ ] 2.3 Remove `AriusComplete` from `BlobConstants` metadata keys
- [ ] 2.4 Update HEAD check logic: use `arius-type` presence as sole crash-recovery signal (remove `arius-complete` check)

## 3. Streaming Enumeration

- [ ] 3.1 Rewrite `LocalFileEnumerator.Enumerate()` to single-pass streaming with `File.Exists()` pairing (no dictionaries, no state)
- [ ] 3.2 Remove `.ToList()` call on enumeration result in `ArchivePipelineHandler`
- [ ] 3.3 Unit test single-pass enumeration: binary+pointer, binary-only, pointer-only, pointer-with-binary-skipped

## 4. Streaming Upload Chain

- [ ] 4.1 Replace `GzipEncryptToMemoryAsync` with streaming chain: `ProgressStream(FileStream) -> GZipStream -> EncryptingStream -> CountingStream -> OpenWriteAsync`
- [ ] 4.2 Add `SetMetadataAsync` call after stream close for large file metadata (`arius-type`, `original-size`, `chunk-size` from `CountingStream.BytesWritten`)
- [ ] 4.3 Update tar upload to use the same streaming chain: `FileStream(tarTempFile) -> GZipStream -> EncryptingStream -> CountingStream -> OpenWriteAsync`
- [ ] 4.4 Update thin chunk creation to write metadata via `SetMetadataAsync` (remove `arius-complete`)

## 5. Dedup and Tar Hash

- [ ] 5.1 Remove `DedupBatchSize` constant and batch/flush pattern; call `_index.LookupAsync([hash])` immediately per file
- [ ] 5.2 Replace `SHA256.HashDataAsync(fs)` with `_encryption.ComputeHashAsync(fs)` for tar hash computation

## 6. Parallel.ForEachAsync Migration

- [ ] 6.1 Replace `Enumerable.Range` + `Task.Run` worker pattern with `Parallel.ForEachAsync(channel.Reader.ReadAllAsync(), ...)` for hash workers
- [ ] 6.2 Replace worker pattern with `Parallel.ForEachAsync` for large upload workers
- [ ] 6.3 Replace worker pattern with `Parallel.ForEachAsync` for tar upload workers

## 7. Integration Tests

- [ ] 7.1 Update crash-recovery tests: assert `arius-type` presence (not `arius-complete`) for recovery detection
- [ ] 7.2 Integration test for streaming upload chain: verify large file roundtrip (archive + restore) with streaming
- [ ] 7.3 Integration test for streaming enumeration: verify pipeline processes files before enumeration completes
