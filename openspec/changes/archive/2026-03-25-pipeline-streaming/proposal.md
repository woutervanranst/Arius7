## Why

The archive pipeline has several efficiency and correctness issues: file enumeration materializes the entire file list before processing starts (`.ToList()`), large file uploads buffer the entire gzip+encrypted content in a `MemoryStream` (OOM risk for multi-GB files), the dedup stage batches 512 items adding pipeline latency without I/O benefit (L1 cache already amortizes shard loads), tar hashes use raw `SHA256` instead of `_encryption.ComputeHashAsync()` (inconsistent with content hashes), and the `arius-complete` metadata key is redundant with `arius-type` for crash recovery. These issues should be addressed together as they touch the same pipeline code paths.

## What Changes

- **Streaming file enumeration**: Replace the two-pass collect-then-pair `Enumerate()` with a single-pass streaming implementation. When encountering a binary file, check if the pointer exists via `File.Exists()` and yield the `FilePair` immediately. When encountering a pointer file, check if the binary exists — if yes, skip (already emitted); if no, yield as pointer-only. Remove the `.ToList()` call in the pipeline handler. Emit an indeterminate `FileScannedEvent` as files are discovered, then emit the final count once enumeration completes.
- **Streaming upload via `OpenWriteAsync`**: Replace `GzipEncryptToMemoryAsync` (which buffers entire compressed+encrypted content in RAM) with a fully streaming chain: `ProgressStream(FileStream) → GZipStream → EncryptingStream → CountingStream → OpenWriteAsync`. Add `OpenWriteAsync` to `IBlobStorageService`. The `ProgressStream` (read-mode wrapper on source) reports `IProgress<long>` with source bytes read for real progress indication. The `CountingStream` (write-mode wrapper on Azure stream) tracks `BytesWritten` for `IndexEntry.CompressedSize`. Metadata (`AriusType`, `OriginalSize`, `ChunkSize`) is written via `SetMetadataAsync` after the stream is closed, preserving the crash-recovery invariant (metadata present = upload complete).
- **Drop dedup batch mechanism**: Remove `DedupBatchSize` constant and the batch/flush pattern. Process each hashed file immediately through `_index.LookupAsync([hash])`. The L1 LRU cache already amortizes repeated shard loads for the same prefix — batching adds pipeline latency for zero I/O benefit.
- **Remove `AriusComplete` metadata key**: Stop writing `arius-complete` metadata. Use `arius-type` presence as the sole crash-recovery signal. Since both keys are written atomically in the same `SetMetadataAsync` call (after upload), checking `arius-type` is sufficient. Update crash-recovery HEAD checks accordingly. Note: this changes the crash-recovery detection mechanism, but there are no existing production archives requiring backward compatibility.
- **Seed tar hash with passphrase**: Replace `SHA256.HashDataAsync(fs)` in the tar builder with `_encryption.ComputeHashAsync(fs)` for consistency with content hash computation. This means tar hashes are passphrase-seeded when a passphrase is provided.
- **Switch to `Parallel.ForEachAsync`**: Replace the `Enumerable.Range(0, N).Select(_ => Task.Run(...))` worker pattern for channel consumers with `Parallel.ForEachAsync` for hash workers, large upload workers, and tar upload workers.

## Capabilities

### New Capabilities
- `streaming-upload`: Covers the `ProgressStream` and `CountingStream` wrappers, the `OpenWriteAsync` abstraction on `IBlobStorageService`, and the fully streaming upload chain for both large files and tar bundles.

### Modified Capabilities
- `archive-pipeline`: Streaming enumeration (single-pass, no `.ToList()`), drop dedup batch, remove `arius-complete`, seed tar hash with passphrase, switch to `Parallel.ForEachAsync`.
- `blob-storage`: Add `OpenWriteAsync` to `IBlobStorageService`. Remove `arius-complete` from metadata keys. Metadata written after upload (not atomically with upload).

## Impact

- **Arius.Core**: `ArchivePipelineHandler` (major refactor of enumeration, dedup, upload stages), `LocalFileEnumerator` (rewrite to single-pass streaming), new `ProgressStream` and `CountingStream` utility classes, `BlobConstants` (remove `AriusComplete` key).
- **Arius.Core/Storage**: `IBlobStorageService` gets new `OpenWriteAsync` method.
- **Arius.AzureBlob**: `AzureBlobStorageService` implements `OpenWriteAsync` via `BlockBlobClient.OpenWriteAsync()`.
- **Crash recovery**: HEAD check changes from `arius-complete == "true"` to `arius-type` presence. Metadata written after upload via separate `SetMetadataAsync` call.
- **Backward compatibility**: No existing production archives — backward compatibility is not a constraint. The `arius-complete` key removal and tar hash change (passphrase-seeded) are clean breaks with no migration path needed.
- **Tests**: Crash recovery tests need updating for the new metadata check. Integration tests for streaming upload chain. Unit tests for `ProgressStream` and `CountingStream`.
