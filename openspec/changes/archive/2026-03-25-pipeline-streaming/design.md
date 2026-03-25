## Context

The archive pipeline in `ArchivePipelineHandler.cs` processes files through a multi-stage channel-based pipeline: Enumerate → Hash → Dedup → Route → Upload. Several stages have efficiency issues:

1. **Enumeration** (`LocalFileEnumerator.cs`): Two-pass collect-then-pair approach materializes all files into dictionaries before yielding any `FilePair`. The pipeline handler then calls `.ToList()`, forcing full materialization before any downstream work begins.
2. **Upload** (`GzipEncryptToMemoryAsync`): Buffers entire gzip+encrypted content in a `MemoryStream` before uploading. For multi-GB files this causes OOM.
3. **Dedup batching**: Accumulates 512 hashed items before performing any index lookup, adding latency. The L1 LRU cache already amortizes shard loads, making batch amortization redundant.
4. **Crash recovery**: Uses both `arius-complete` and `arius-type` metadata keys redundantly. Since metadata is written atomically after upload, `arius-type` presence alone is sufficient.
5. **Tar hashing**: Uses raw `SHA256.HashDataAsync()` instead of `_encryption.ComputeHashAsync()`, inconsistent with content hash computation.
6. **Worker pattern**: Uses `Enumerable.Range(0, N).Select(_ => Task.Run(...))` instead of the more idiomatic `Parallel.ForEachAsync`.

The `IBlobStorageService` interface currently only supports `UploadAsync(stream)` which requires a readable, seekable stream. Azure's `BlockBlobClient.OpenWriteAsync()` returns a writable stream that enables fully streaming uploads without buffering.

## Goals / Non-Goals

**Goals:**
- Streaming file enumeration: yield `FilePair` objects as files are discovered, no materialization
- Streaming upload: eliminate the in-memory buffer for gzip+encrypted content, supporting arbitrarily large files
- Provide `IProgress<long>` on source bytes read for per-file progress reporting
- Track compressed bytes written for `IndexEntry.CompressedSize`
- Remove pipeline latency from dedup batching
- Simplify crash-recovery metadata to a single key
- Consistent hash computation across content hashes and tar hashes
- Idiomatic concurrency with `Parallel.ForEachAsync`

**Non-Goals:**
- Backward compatibility with existing archives (no production archives exist)
- Changing the channel-based pipeline topology (stages remain the same)
- Modifying the chunk index or shard format
- Console progress display (separate change: `console-progress`)

## Decisions

### 1. Single-pass streaming enumeration

**Decision**: Replace the two-pass collect-then-pair approach with a single-pass depth-first walk that uses `File.Exists()` checks to pair binary/pointer files on the fly.

**Rationale**: Binary and pointer files always reside in the same directory by convention (pointer = binary + ".pointer.arius"). When encountering a binary file, `File.Exists(binary + ".pointer.arius")` is a cheap syscall (same directory, OS-cached). When encountering a pointer file, `File.Exists(pointer[..^".pointer.arius".Length])` checks for the binary — if it exists, skip (already emitted as part of the binary's pair); if not, yield as pointer-only. No state tracking needed.

**Alternative considered**: Per-directory batching (collect all files in one directory, pair, then yield). Rejected because the `File.Exists()` approach is simpler, requires no buffering at all, and handles all three cases (binary+pointer, binary-only, pointer-only) without any collection.

### 2. Streaming upload via `OpenWriteAsync` + `CountingStream` + `ProgressStream`

**Decision**: Add `OpenWriteAsync` to `IBlobStorageService` returning a writable `Stream`. The upload chain becomes:

```
ProgressStream(FileStream) → GZipStream → EncryptingStream → CountingStream → OpenWriteAsync
```

Two new stream wrappers:
- `ProgressStream`: Read-mode wrapper on the source `FileStream`. Reports `IProgress<long>` with cumulative source bytes read. The total is known (`FileInfo.Length`), enabling deterministic progress bars.
- `CountingStream`: Write-mode wrapper on the Azure write stream. Tracks `BytesWritten` for `IndexEntry.CompressedSize` and `SetMetadataAsync`.

**Rationale**: `EncryptingStream` is already write-mode. `GZipStream` in compress mode writes to its inner stream. `OpenWriteAsync` returns a writable stream. The entire chain is push-direction (write), so it composes naturally. No pipes, temp files, or memory buffers needed.

**Alternative considered**: Using `BlockBlobOpenWriteOptions.ProgressHandler` for progress and compressed size. Rejected because: (a) it reports compressed bytes, not source bytes (user cares about file progress, not compressed bytes), (b) the `IBlobStorageService` abstraction shouldn't leak Azure SDK options, (c) `CountingStream` is ~20 lines and gives both values from one place.

### 3. Metadata written after upload via `SetMetadataAsync`

**Decision**: Upload the blob body first (via `OpenWriteAsync` stream), then write metadata (`AriusType`, `OriginalSize`, `ChunkSize`) via a separate `SetMetadataAsync` call after the stream is closed.

**Rationale**: This preserves the crash-recovery invariant: metadata present = upload complete. If crash happens during upload, the blob has no metadata → re-upload on next run. If crash happens between upload and metadata write, the blob is complete but no metadata → re-upload (safe, idempotent). `ChunkSize` (compressed size) isn't known until the stream is closed, so it can't be set upfront.

### 4. Drop `arius-complete`, use `arius-type` as sole crash-recovery signal

**Decision**: Stop writing `arius-complete` metadata. HEAD checks for crash recovery change from `metadata["arius-complete"] == "true"` to `metadata.ContainsKey("arius-type")`.

**Rationale**: Both keys are written in the same `SetMetadataAsync` call (atomically from Azure's perspective). If `arius-type` is present, the upload completed. The `arius-complete` key adds no safety. No existing production archives need backward compatibility.

### 5. Drop dedup batch, process immediately

**Decision**: Remove `DedupBatchSize` (512) and the `batch`/`FlushBatch()` pattern. Each hashed file is looked up immediately via `_index.LookupAsync([hash])`.

**Rationale**: The L1 LRU cache serves repeated shard prefix lookups from memory. With L1, batching provides zero I/O benefit (the first lookup for a prefix loads the shard, subsequent lookups hit L1). Batching only adds pipeline latency — downstream stages (upload, tar) are idle while 512 items accumulate. The `LookupAsync` method already handles single-hash lookups efficiently (groups by prefix internally, loads one shard).

### 6. Seed tar hash with passphrase

**Decision**: Replace `SHA256.HashDataAsync(fs)` with `_encryption.ComputeHashAsync(fs)` for tar hash computation.

**Rationale**: Content hashes already use `_encryption.ComputeHashAsync()` which prepends the passphrase bytes. Using raw SHA256 for tar hashes is inconsistent. With passphrase: `SHA256(passphrase + data)`. Without passphrase: `SHA256(data)` (unchanged). No backward compatibility concern.

### 7. `Parallel.ForEachAsync` for channel consumers

**Decision**: Replace `Enumerable.Range(0, N).Select(_ => Task.Run(async () => { await foreach ... })).ToArray()` with `Parallel.ForEachAsync(channel.Reader.ReadAllAsync(), new ParallelOptions { MaxDegreeOfParallelism = N }, ...)`.

**Rationale**: More idiomatic, handles exceptions better (no fire-and-forget tasks), and respects `CancellationToken` natively. `Parallel.ForEachAsync` on an `IAsyncEnumerable<T>` (from `ReadAllAsync()`) is the intended pattern in modern .NET.

## Risks / Trade-offs

- **`File.Exists()` per file during enumeration** → Two extra syscalls per file (one for pointer check, one for binary check). On modern OS/filesystem with directory entries cached, this is negligible. If profiling shows it's a bottleneck on network filesystems, the per-directory batching approach is a fallback. → Mitigation: benchmark on NFS/SMB if needed later.
- **`OpenWriteAsync` creates the blob immediately** → The blob exists (possibly empty or partial) before metadata is written. If another process reads the blob between creation and metadata write, it sees no metadata. → Mitigation: the only reader is the restore pipeline, which checks metadata before downloading. Crash recovery re-checks on next run.
- **`SetMetadataAsync` is a separate API call** → One extra HTTP round-trip per upload. For large files (network-bound), this is negligible. For many small files in tars, the extra call is per-tar (not per-file). → Mitigation: acceptable overhead.
- **`Parallel.ForEachAsync` schedules differently than manual `Task.Run`** → Worker count is a soft limit (degree of parallelism), not exact thread count. Behavior is equivalent for I/O-bound work. → Mitigation: test validates concurrent upload count.
