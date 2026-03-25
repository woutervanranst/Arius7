# Archive Pipeline Spec

## Purpose

Defines the archive pipeline for Arius: file enumeration, hashing, deduplication, size-based routing, large file upload, tar bundling, thin chunk creation, index shard management, Merkle tree construction, snapshot creation, pointer files, and crash recovery.

## Requirements

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

### Requirement: Streaming hash computation
The system SHALL compute content hashes by streaming file data through the hash function without loading the entire file into memory. The hash function SHALL be SHA256(data) in plaintext mode or SHA256(passphrase + data) in encrypted mode (literal byte concatenation). Pointer file hashes SHALL NEVER be trusted as a cache — every binary file SHALL be re-hashed on every archive run.

#### Scenario: Large file hashing
- **WHEN** a 10 GB binary file is hashed
- **THEN** the system SHALL compute the hash using streaming with bounded memory (stream buffer only, no full file load)

#### Scenario: Binary exists with stale pointer
- **WHEN** a binary file has a pointer file whose hash does not match the computed binary hash
- **THEN** the system SHALL use the computed binary hash (not the pointer hash) and mark the pointer as stale for overwriting

#### Scenario: Pointer-only file (thin archive)
- **WHEN** only a pointer file exists (no binary)
- **THEN** the system SHALL use the hash from the pointer file without re-hashing

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

### Requirement: Size-based routing
The system SHALL route files to the large file pipeline if their size is >= `--small-file-threshold` (default 1 MB) and to the tar builder if their size is < the threshold.

#### Scenario: File at threshold boundary
- **WHEN** a file is exactly 1,048,576 bytes (1 MB, the default threshold)
- **THEN** the system SHALL route it to the large file pipeline

#### Scenario: Small file routing
- **WHEN** a file is 500 bytes
- **THEN** the system SHALL route it to the tar builder

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

### Requirement: Index shard merge and upload
The system SHALL collect all new index entries (content-hash → chunk-hash, original-size, compressed-size) and upload updated chunk index shards once at the end of the archive run. For each modified shard prefix, the system SHALL download the existing shard (if cached or from Azure), merge new entries, and upload.

The L3 wire format (Azure blobs) SHALL be: plaintext lines → gzip compressed → optionally AES-256-CBC encrypted. Content type SHALL be `application/aes256cbc+gzip` (encrypted) or `application/gzip` (plaintext).

The L2 local disk cache SHALL store shards as **plaintext lines only** — no gzip compression, no encryption. This makes the local cache human-readable and avoids unnecessary CPU overhead on every cache read/write. The L2 format SHALL NOT change based on whether a passphrase is provided.

The shard entry format SHALL use a field-count convention to distinguish large and small files:
- **Large file entries** (content-hash equals chunk-hash) SHALL be serialized as 3 space-separated fields: `<content-hash> <original-size> <compressed-size>\n`.
- **Small file entries** (content-hash differs from chunk-hash) SHALL be serialized as 4 space-separated fields: `<content-hash> <chunk-hash> <original-size> <compressed-size>\n`.

On parsing, the system SHALL reconstruct the in-memory entry for 3-field lines by setting the chunk-hash equal to the content-hash. The in-memory data model SHALL remain unchanged (4 properties: content-hash, chunk-hash, original-size, compressed-size).

#### Scenario: Large file entry serialized as 3 fields
- **WHEN** a shard entry has content-hash equal to chunk-hash (large file)
- **THEN** the entry SHALL be serialized as `<content-hash> <original-size> <compressed-size>` (3 space-separated fields)

#### Scenario: Small file entry serialized as 4 fields
- **WHEN** a shard entry has content-hash different from chunk-hash (tar-bundled file)
- **THEN** the entry SHALL be serialized as `<content-hash> <chunk-hash> <original-size> <compressed-size>` (4 space-separated fields)

#### Scenario: Parsing a 3-field entry
- **WHEN** a shard line contains exactly 3 space-separated fields
- **THEN** the system SHALL parse it as a large file entry and set chunk-hash equal to content-hash in the in-memory model

#### Scenario: Parsing a 4-field entry
- **WHEN** a shard line contains exactly 4 space-separated fields
- **THEN** the system SHALL parse it as a small file entry with an explicit chunk-hash

#### Scenario: New entries merged into existing shard
- **WHEN** 50 new files have content hashes with prefix `a1`
- **THEN** the system SHALL download/load the `a1` shard, append 50 entries, and upload the merged shard to Azure in gzip+encrypt format

#### Scenario: First archive (no existing shards)
- **WHEN** archiving to an empty repository
- **THEN** the system SHALL create new shards for each prefix that has entries

#### Scenario: L2 cache stores plaintext
- **WHEN** a shard is saved to the local L2 disk cache
- **THEN** the file SHALL contain raw plaintext lines with no compression or encryption, regardless of whether a passphrase is configured

#### Scenario: L3 upload uses wire format
- **WHEN** a shard is uploaded to Azure
- **THEN** the blob SHALL be gzip-compressed and AES-256-CBC encrypted if a passphrase is provided, or gzip-compressed only if no passphrase is provided

#### Scenario: Stale L2 file is self-healing
- **WHEN** an L2 cache file cannot be parsed as plaintext lines (e.g., it contains old encrypted bytes from a prior version)
- **THEN** the system SHALL treat it as a cache miss, fall through to L3, and re-cache the shard in plaintext format

### Requirement: Merkle tree construction
The system SHALL build a content-addressed merkle tree of directories after all uploads complete. Tree construction SHALL use a two-phase approach: (1) write completed file entries to an unsorted manifest temp file during the pipeline, (2) external sort by path, then stream through building tree blobs bottom-up one directory at a time.

Each tree blob SHALL be a text file with one entry per line, sorted by name (ordinal, case-sensitive). File entries SHALL use the format: `<hash> F <created> <modified> <name>`. Directory entries SHALL use the format: `<hash> D <name>`. Directory names SHALL have a trailing `/`. Timestamps SHALL use ISO-8601 round-trip format (`"O"` format specifier, UTC). Lines SHALL be terminated by `\n`.

File size SHALL NOT be stored in tree blobs (it is in the chunk index). Empty directories SHALL be skipped. Unchanged tree blobs SHALL be deduplicated by content hash.

#### Scenario: Directory with files produces tree blob
- **WHEN** directory `photos/2024/june/` contains files `a.jpg` and `b.jpg`
- **THEN** a tree blob SHALL be created with 2 file entries in text format and uploaded to `filetrees/<tree-hash>`

#### Scenario: File entry format
- **WHEN** a file `vacation.jpg` with hash `abc123...` created `2026-03-25T10:00:00.0000000+00:00` and modified `2026-03-25T12:30:00.0000000+00:00` is included in a tree blob
- **THEN** the entry SHALL be serialized as `abc123... F 2026-03-25T10:00:00.0000000+00:00 2026-03-25T12:30:00.0000000+00:00 vacation.jpg`

#### Scenario: Directory entry format
- **WHEN** a subdirectory `2024 trip/` with tree-hash `def456...` is included in a tree blob
- **THEN** the entry SHALL be serialized as `def456... D 2024 trip/`

#### Scenario: Entries sorted by name
- **WHEN** a directory contains entries `b.jpg`, `a.jpg`, and `subdir/`
- **THEN** the tree blob SHALL list entries sorted by name using ordinal case-sensitive comparison

#### Scenario: Filename with spaces
- **WHEN** a file is named `my vacation photo.jpg`
- **THEN** the name SHALL appear as-is after the last fixed field (no quoting or escaping required)

#### Scenario: Unchanged directory across runs
- **WHEN** directory `documents/` has identical files and metadata between two archive runs
- **THEN** the tree blob hash SHALL be identical and the blob SHALL NOT be re-uploaded

#### Scenario: Empty directory skipped
- **WHEN** a directory contains no files (directly or in subdirectories)
- **THEN** no tree blob SHALL be created for that directory

#### Scenario: Tree hash computation
- **WHEN** a tree blob is serialized to text
- **THEN** the tree hash SHALL be SHA256 of the UTF-8 encoded text bytes, optionally passphrase-seeded via `IEncryptionService.ComputeHash`

### Requirement: Snapshot creation
The system SHALL create a snapshot manifest after tree construction completes. The snapshot SHALL be stored at `snapshots/<UTC-timestamp>` (e.g., `2026-03-22T150000.000Z`) and SHALL contain the root tree hash, timestamp, file count, total size, and Arius version. Snapshots SHALL NEVER be deleted.

#### Scenario: Successful snapshot creation
- **WHEN** the archive pipeline completes successfully
- **THEN** a snapshot manifest SHALL be uploaded to `snapshots/<timestamp>` with the root tree hash and metadata

#### Scenario: Snapshot reflects current state
- **WHEN** files have been added, modified, and deleted since the last archive
- **THEN** the new snapshot SHALL only contain files that exist at archive time (no deleted files)

### Requirement: Pointer file writing
The system SHALL create or update `.pointer.arius` files alongside binary files (unless `--no-pointers` is set). The pointer file SHALL contain the hex content hash of the binary. If a pointer exists but is stale (hash mismatch), it SHALL be overwritten. Pointer writing SHALL run in parallel.

#### Scenario: New pointer file creation
- **WHEN** a binary file `report.pdf` is archived and no pointer exists
- **THEN** the system SHALL create `report.pdf.pointer.arius` containing the content hash

#### Scenario: Stale pointer overwritten
- **WHEN** a binary file's computed hash differs from the existing pointer file content
- **THEN** the pointer file SHALL be overwritten with the correct hash

#### Scenario: --no-pointers flag
- **WHEN** the archive is run with `--no-pointers`
- **THEN** no pointer files SHALL be created or updated

### Requirement: --remove-local flag
The system SHALL delete local binary files after successful archive when `--remove-local` is set, leaving only the pointer files. This creates a "thin" archive.

#### Scenario: Remove local after archive
- **WHEN** archive completes successfully with `--remove-local`
- **THEN** all archived binary files SHALL be deleted and only `.pointer.arius` files SHALL remain

#### Scenario: Remove local requires pointers
- **WHEN** `--remove-local` is used with `--no-pointers`
- **THEN** the system SHALL reject the combination (cannot remove binaries without pointers to track them)

### Requirement: Crash-recoverable archive
The archive pipeline SHALL use optimistic concurrency for all chunk uploads: uploads are attempted unconditionally without a pre-flight HEAD check.

`OpenWriteAsync` and `UploadAsync(overwrite:false)` use create-if-not-exists semantics (IfNoneMatch=*). If the blob already exists, `BlobAlreadyExistsException` is raised.

On catching `BlobAlreadyExistsException`, the pipeline SHALL perform a HEAD check (GetMetadataAsync) to determine blob completeness using the `arius-type` metadata sentinel:
- `arius-type` present → blob is fully committed (body + metadata); recover ContentLength as compressedSize and continue without re-uploading
- `arius-type` absent → blob body was committed but metadata was not yet written (partial state); delete the blob and retry the upload from scratch (goto retry)

This pattern applies to all three upload sub-stages: large file upload (Stage 4a), tar blob upload (Stage 4c-tar), and thin chunk creation (Stage 4c-thin).

#### Scenario: Re-run after crash - fully committed blob
- **WHEN** a crash-recovery re-run encounters a fully committed blob (BlobAlreadyExistsException + arius-type present)
- **THEN** the pipeline SHALL recover compressedSize from ContentLength and continue without re-uploading

#### Scenario: Re-run after crash - partially committed blob
- **WHEN** a crash-recovery re-run encounters a partially committed blob (BlobAlreadyExistsException + arius-type absent)
- **THEN** the pipeline SHALL delete the blob and retry the upload

#### Scenario: Thin chunk already complete
- **WHEN** `UploadAsync(overwrite:false)` raises BlobAlreadyExistsException for a thin chunk and arius-type is present
- **THEN** the pipeline SHALL skip silently (fully complete)

#### Scenario: Thin chunk partially committed
- **WHEN** `UploadAsync(overwrite:false)` raises BlobAlreadyExistsException for a thin chunk and arius-type is absent
- **THEN** the pipeline SHALL delete the thin blob and retry

#### Scenario: Clean run (no crash)
- **WHEN** no crash occurred (normal run)
- **THEN** dedup (Stage 2) filters known hashes so Stage 4 never encounters existing blobs; the BlobAlreadyExistsException path is never exercised

### Requirement: Configurable storage tier
The system SHALL upload chunks to the tier specified by `--tier` (default: Archive). Supported tiers: Hot, Cool, Cold, Archive.

#### Scenario: Archive tier upload
- **WHEN** `--tier Archive` is set (or defaulted)
- **THEN** chunk blobs SHALL be uploaded to Archive tier

#### Scenario: Hot tier upload
- **WHEN** `--tier Hot` is set
- **THEN** chunk blobs SHALL be uploaded to Hot tier (no rehydration needed for restore)

### Requirement: OS-neutral path handling
The system SHALL normalize all paths to forward slashes (`/`) for storage. Backslashes in Windows paths SHALL be converted. Reserved characters in filenames SHALL be handled gracefully. Paths stored in snapshots, tree blobs, and the chunk index SHALL always use forward slashes.

#### Scenario: Windows-style path archival
- **WHEN** archiving on Windows with path `documents\2024\report.pdf`
- **THEN** the path stored in the snapshot tree SHALL be `documents/2024/report.pdf`

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
