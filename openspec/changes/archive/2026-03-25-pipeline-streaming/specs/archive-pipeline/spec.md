## MODIFIED Requirements

### Requirement: File enumeration
The system SHALL recursively enumerate all files in the local root directory, producing FilePair units for archiving using a single-pass streaming approach. Files with the `.pointer.arius` suffix SHALL always be treated as pointer files. All other files SHALL be treated as binary files. If a file cannot be read (e.g., system-protected), the system SHALL log a warning and continue with the remaining files. Enumeration SHALL be depth-first to provide directory affinity for the tar builder. Enumeration SHALL yield FilePair objects immediately as files are discovered without materializing the full file list into memory. When encountering a binary file, the system SHALL check `File.Exists(binaryPath + ".pointer.arius")` to pair it. When encountering a pointer file, the system SHALL check `File.Exists(pointerPath[..^".pointer.arius".Length])` -- if the binary exists, skip (already emitted with the binary); if not, yield as pointer-only. No dictionaries or state tracking SHALL be used.

#### Scenario: Binary file with matching pointer
- **WHEN** a binary file `photos/vacation.jpg` exists alongside `photos/vacation.jpg.pointer.arius`
- **THEN** the system SHALL produce a FilePair with both binary and pointer present, discovered via `File.Exists` check on the binary

#### Scenario: Binary file without pointer
- **WHEN** a binary file `documents/report.pdf` exists with no corresponding `.pointer.arius` file
- **THEN** the system SHALL produce a FilePair with binary present and pointer absent

#### Scenario: Pointer file without binary (thin archive)
- **WHEN** a pointer file `music/song.mp3.pointer.arius` exists with no corresponding binary
- **THEN** the system SHALL produce a FilePair with pointer present and binary absent, using the hash from the pointer file

#### Scenario: Pointer file with binary already emitted
- **WHEN** a pointer file `photos/vacation.jpg.pointer.arius` is encountered and `photos/vacation.jpg` exists
- **THEN** the system SHALL skip the pointer file (it was already emitted as part of the binary's FilePair)

#### Scenario: Inaccessible file
- **WHEN** a file cannot be read due to permissions or system protection
- **THEN** the system SHALL log a warning with the file path and reason, skip the file, and continue enumeration

#### Scenario: Pointer file with invalid content
- **WHEN** a `.pointer.arius` file contains content that is not a valid hex hash
- **THEN** the system SHALL log a warning and treat the file as having no valid pointer

#### Scenario: No materialization of file list
- **WHEN** enumerating a directory with 1 million files
- **THEN** the pipeline SHALL begin processing the first FilePair before enumeration completes, with no `.ToList()` or equivalent materialization

### Requirement: Dedup check against chunk index
The system SHALL check each content hash against the chunk index before uploading. Each hashed file SHALL be looked up immediately via `_index.LookupAsync([hash])` without batching. The L1 LRU cache SHALL amortize repeated shard loads for the same prefix. An in-flight set SHALL prevent duplicate uploads of the same content hash within a single archive run. The dedup stage SHALL be single-threaded to manage the in-flight set without locking.

#### Scenario: Content hash already in index
- **WHEN** a file's content hash already exists in the chunk index
- **THEN** the system SHALL skip the upload and include the file in the snapshot tree

#### Scenario: Content hash not in index
- **WHEN** a file's content hash is not in the chunk index
- **THEN** the system SHALL route the file to the upload pipeline (large or tar based on size)

#### Scenario: Duplicate content hash in same run
- **WHEN** two files with identical content (same hash) are processed in the same archive run
- **THEN** the system SHALL upload the content only once (the second file hits the in-flight set) and both files SHALL appear in the snapshot tree

#### Scenario: Pointer-only file with missing chunk
- **WHEN** a pointer-only file references a content hash that is not in the chunk index
- **THEN** the system SHALL log a warning and exclude the file from the snapshot

#### Scenario: Immediate lookup without batching
- **WHEN** a file is hashed and enters the dedup stage
- **THEN** the system SHALL look it up immediately without waiting for a batch to accumulate

### Requirement: Large file upload
The system SHALL upload large files individually as chunks using streaming upload. The upload pipeline SHALL use the streaming chain: `ProgressStream(FileStream) -> GZipStream -> EncryptingStream -> CountingStream -> OpenWriteAsync` to stream data to `chunks/<content-hash>`. The blob metadata SHALL include `arius-type: large`, `original-size`, and `chunk-size` (from `CountingStream.BytesWritten`), written via `SetMetadataAsync` after the upload stream closes. Content type SHALL be set to `application/aes256cbc+gzip` (encrypted) or `application/gzip` (plaintext). Multiple large files SHALL upload concurrently using `Parallel.ForEachAsync`.

#### Scenario: Large file upload with encryption
- **WHEN** a 50 MB file is uploaded with a passphrase
- **THEN** the system SHALL stream the file through ProgressStream -> GZipStream -> EncryptingStream -> CountingStream -> OpenWriteAsync to `chunks/<content-hash>` with content type `application/aes256cbc+gzip`

#### Scenario: Large file upload without encryption
- **WHEN** a 50 MB file is uploaded without a passphrase
- **THEN** the system SHALL stream the file through the chain with content type `application/gzip`

#### Scenario: Metadata written after upload
- **WHEN** a chunk upload finishes
- **THEN** the blob metadata SHALL have `arius-type: large`, `original-size`, and `chunk-size` set via `SetMetadataAsync`

#### Scenario: Concurrent uploads
- **WHEN** 10 large files need uploading with 4 upload workers
- **THEN** the system SHALL process them via `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 4`

### Requirement: Tar builder
The system SHALL bundle small files into tar archives using a single tar builder. Files inside the tar SHALL be named by their content-hash (not original path). The tar builder SHALL seal and hand off the tar to the upload channel when the accumulated uncompressed size reaches `--tar-target-size` (default 64 MB). After sealing, the builder SHALL immediately start a new tar. The tar builder SHALL stream to a temp file on disk (not memory). Depth-first enumeration provides natural directory affinity. The tar hash SHALL be computed using `_encryption.ComputeHashAsync(fs)` (passphrase-seeded when a passphrase is provided) for consistency with content hash computation.

#### Scenario: Tar sealing at target size
- **WHEN** accumulated small files in the current tar reach 64 MB uncompressed
- **THEN** the system SHALL seal the tar, compute its tar-hash via `_encryption.ComputeHashAsync`, and hand it off for upload

#### Scenario: Partial tar at end of archive
- **WHEN** the archive run completes with a partially filled tar (< 64 MB)
- **THEN** the system SHALL seal and upload the partial tar

#### Scenario: Tar with single file
- **WHEN** only one small file needs archiving
- **THEN** the system SHALL create a tar with that single file entry

#### Scenario: Tar upload format
- **WHEN** a sealed tar is uploaded
- **THEN** the blob SHALL be stored at `chunks/<tar-hash>` with `arius-type: tar` and content type `application/aes256cbc+tar+gzip` (or `application/tar+gzip` without passphrase)

#### Scenario: Tar hash uses passphrase-seeded hash
- **WHEN** a tar is sealed with a passphrase configured
- **THEN** the tar-hash SHALL be `SHA256(passphrase + tarBytes)` via `_encryption.ComputeHashAsync`

### Requirement: Thin chunk creation
The system SHALL create a thin chunk blob for each small file after its tar is successfully uploaded. The thin chunk SHALL be stored at `chunks/<content-hash>` with body containing the tar-hash (plain text). Blob metadata SHALL include `arius-type: thin`, `original-size`, and `compressed-size` (proportional estimate based on the tar's compression ratio), written via `SetMetadataAsync` after upload.

#### Scenario: Thin chunk for tar-bundled file
- **WHEN** file with content-hash `abc123` is bundled in tar with hash `def456`
- **THEN** a blob SHALL be created at `chunks/abc123` with body `def456`, `arius-type: thin`

#### Scenario: Thin chunk enables crash recovery
- **WHEN** archive crashes after tar upload but before index update, and the archive is re-run
- **THEN** the system SHALL discover existing thin chunks via HEAD, read the tar-hash from the body, and recover the index mapping without re-uploading

### Requirement: Crash-recoverable archive
Archive runs SHALL be idempotent on re-run after a crash. Each uploaded chunk blob SHALL carry `arius-type` metadata set via `SetMetadataAsync` after the body upload completes. On re-run, the system SHALL HEAD-check each content-hash before uploading: 200 + `arius-type` present -> skip and recover index entry; 200 without `arius-type` -> overwrite (incomplete upload); 404 -> upload fresh. The `arius-complete` metadata key SHALL NOT be used. The presence of `arius-type` is the sole crash-recovery signal.

#### Scenario: Re-run after crash during uploads
- **WHEN** archive crashes after uploading 50 of 100 chunks and is re-run
- **THEN** the system SHALL detect the 50 completed chunks via HEAD + `arius-type` presence, skip their upload, recover their index entries, and upload only the remaining 50

#### Scenario: Re-run after crash during thin chunk creation
- **WHEN** archive crashes after uploading a tar but before creating all thin chunks
- **THEN** files with existing thin chunks SHALL be recovered; files without thin chunks SHALL be re-bundled into a new tar

#### Scenario: Incomplete upload detected
- **WHEN** a blob exists at `chunks/<hash>` with no metadata (upload completed but metadata not yet written)
- **THEN** the system SHALL overwrite the blob with a fresh upload

#### Scenario: Clean re-run (no crash)
- **WHEN** archive is re-run on unchanged files with no prior crash
- **THEN** the HEAD checks SHALL return 404 (files not yet in this run) or dedup check SHALL find them in the index; no performance regression from crash recovery logic

### Requirement: Concurrent pipeline with Parallel.ForEachAsync
The archive pipeline SHALL use `Parallel.ForEachAsync` with `MaxDegreeOfParallelism` for channel consumer stages (hash workers, large upload workers, tar upload workers) instead of `Enumerable.Range(0, N).Select(_ => Task.Run(...))`. `Parallel.ForEachAsync` SHALL consume from `channel.Reader.ReadAllAsync()`. Bounded `Channel<T>` between stages SHALL remain for backpressure.

#### Scenario: Hash workers use Parallel.ForEachAsync
- **WHEN** the hash stage consumes from its input channel
- **THEN** it SHALL use `Parallel.ForEachAsync(channel.Reader.ReadAllAsync(), new ParallelOptions { MaxDegreeOfParallelism = N }, ...)`

#### Scenario: Upload workers use Parallel.ForEachAsync
- **WHEN** the large upload stage consumes from its input channel
- **THEN** it SHALL use `Parallel.ForEachAsync` with the configured worker count

#### Scenario: Cancellation token respected
- **WHEN** the cancellation token is triggered during `Parallel.ForEachAsync`
- **THEN** the operation SHALL terminate gracefully without fire-and-forget tasks

## REMOVED Requirements

### Requirement: Dedup check against chunk index (batching aspects)
**Reason**: The `DedupBatchSize` (512) and batch/flush pattern are removed. The L1 LRU cache already amortizes shard loads, making batch amortization redundant. Batching adds pipeline latency without I/O benefit.
**Migration**: Each hashed file is looked up immediately via `_index.LookupAsync([hash])`. The MODIFIED version of this requirement above replaces the batching behavior.
