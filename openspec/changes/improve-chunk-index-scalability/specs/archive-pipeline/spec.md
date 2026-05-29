## MODIFIED Requirements

### Requirement: Thin chunk creation
The system SHALL create a thin chunk blob for each small file after its tar is successfully uploaded. The thin chunk SHALL be stored at `chunks/<content-hash>` with an empty body. Blob metadata SHALL include `arius_type: thin`, `parent_chunk_hash`, `original_size`, and `compressed_size` (proportional estimate based on the tar's compression ratio), written via `SetMetadataAsync` after upload. The parent tar chunk hash SHALL be stored in `parent_chunk_hash`, not in the thin chunk body.

#### Scenario: Thin chunk for tar-bundled file
- **WHEN** file with content-hash `abc123` is bundled in tar with hash `def456`
- **THEN** a blob SHALL be created at `chunks/abc123` with an empty body
- **AND** metadata SHALL include `arius_type: thin`, `parent_chunk_hash: def456`, `original_size`, and `compressed_size`

#### Scenario: Thin chunk enables repair without body download
- **WHEN** archive crashes after tar upload and thin chunk creation but before index update, and explicit full chunk-index repair is run
- **THEN** repair SHALL reconstruct the index mapping from thin chunk metadata
- **AND** repair SHALL NOT download the thin chunk body to read the parent tar chunk hash

### Requirement: Dedup check against chunk index
The archive pipeline SHALL check each content hash against `ChunkIndexService` before uploading. Each hashed file SHALL be looked up through the chunk index without automatic repair. If chunk-index lookup detects a corrupt remote shard or interrupted local repair state, archive SHALL fail with a clear error that instructs the user to run the explicit chunk-index repair command. An in-flight set SHALL prevent duplicate uploads of the same content hash within a single archive run. The dedup stage SHALL be single-threaded to manage the in-flight set without locking.

The chunk index SHALL be the archive pipeline's fast dedup source, not the only durable source of truth. When the chunk index misses for a binary file, archive SHALL attempt upload using create-if-not-exists storage semantics. If storage reports that a complete chunk blob already exists, archive SHALL recover the chunk metadata from storage, record the missing chunk-index entry, and continue.

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

#### Scenario: Archive lookup fails on corrupt shard
- **WHEN** archive dedup looks up a content hash and the relevant chunk-index shard is corrupt
- **THEN** archive SHALL fail with a clear chunk-index corruption error
- **AND** the error SHALL instruct the user to run the explicit chunk-index repair command

#### Scenario: Archive lookup miss does not trigger prefix repair
- **WHEN** archive dedup looks up a content hash in a valid shard and the content hash is absent
- **THEN** archive SHALL treat the content as not indexed
- **AND** it SHALL NOT trigger prefix-scoped chunk-index repair for that miss

#### Scenario: Existing large chunk recovered after index miss
- **WHEN** archive dedup misses for a large file
- **AND** uploading `chunks/<content-hash>` encounters an existing complete large chunk blob
- **THEN** archive SHALL recover original and compressed size metadata from storage
- **AND** it SHALL record the chunk-index entry for that content hash
- **AND** it SHALL continue without failing the archive

### Requirement: Chunk index flush before snapshot
The archive pipeline SHALL record new chunk metadata in `ChunkIndexService` as chunks are durably uploaded or recovered from complete existing chunk blobs. Before creating the snapshot, the pipeline SHALL flush pending chunk index entries so the published snapshot never references content that cannot be resolved through the chunk index.

#### Scenario: New chunk metadata recorded
- **WHEN** a large, tar, or thin chunk operation completes successfully
- **THEN** the archive pipeline SHALL record the content-hash to chunk-hash mapping with original and compressed sizes through `ChunkIndexService`

#### Scenario: Existing large chunk metadata recorded after collision recovery
- **WHEN** a large chunk upload encounters a complete existing chunk blob after a chunk-index miss
- **THEN** the archive pipeline SHALL record the recovered content-hash to chunk-hash mapping with original and compressed sizes through `ChunkIndexService`

#### Scenario: Thin chunk metadata records parent tar chunk
- **WHEN** the archive pipeline creates a thin chunk for a tar-bundled file
- **THEN** the thin chunk blob SHALL be uploaded with an empty body
- **AND** its metadata SHALL include `arius_type: thin`, `parent_chunk_hash`, `original_size`, and `compressed_size`
- **AND** `parent_chunk_hash` SHALL contain the parent tar chunk hash
- **AND** the archive pipeline SHALL record the same content-hash to parent tar chunk-hash mapping through `ChunkIndexService`

#### Scenario: Chunk index flushed before snapshot
- **WHEN** archive finalization begins after uploads complete
- **THEN** pending chunk index entries SHALL be flushed before snapshot creation

### Requirement: Merkle tree construction
The system SHALL build a content-addressed merkle tree of directories after all uploads complete. Tree construction SHALL use a two-phase approach: (1) write completed file entries to an unsorted manifest temp file during the pipeline, (2) external sort by path, then stream through building tree blobs bottom-up one directory at a time.

Each tree blob SHALL be a text file with one entry per line, sorted by name (ordinal, case-sensitive). File entries SHALL use the format: `<hash> F <created> <modified> <name>`. Directory entries SHALL use the format: `<hash> D <name>`. Directory names SHALL have a trailing `/`. Timestamps SHALL use ISO-8601 round-trip format (`"O"` format specifier, UTC). Lines SHALL be terminated by `\n`.

File size SHALL NOT be stored in tree blobs (it is in the chunk index). Empty directories SHALL be skipped. Unchanged tree blobs SHALL be deduplicated by content hash.

Tree blob uploads and existence checks SHALL use `FileTreeService` instead of ad-hoc disk caching. Specifically:
- `FileTreeBuilder.EnsureUploadedAsync` SHALL use `FileTreeService.ExistsInRemote(hash)` to check whether a tree blob already exists in remote storage, replacing the current `File.Exists` + `GetMetadataAsync` (HEAD) pattern.
- When a new tree blob needs uploading, `FileTreeBuilder` SHALL use `FileTreeService.WriteAsync(hash, treeBlob)` for write-through to both Azure and the disk cache, replacing the current inline `_blobs.UploadAsync` + `File.WriteAllBytes` pattern.
- Filetree cache validation/materialization SHALL be coordinated by `ArchiveCommandHandler` before tree existence checks.

#### Scenario: Directory with files produces tree blob
- **WHEN** directory `photos/2024/june/` contains files `a.jpg` and `b.jpg`
- **THEN** a tree blob SHALL be created with 2 file entries in text format and uploaded via `FileTreeService.WriteAsync` to `filetrees/<tree-hash>`

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

#### Scenario: Unchanged directory across runs (fast path)
- **WHEN** directory `documents/` has identical files and metadata between two archive runs on the same machine
- **THEN** `FileTreeService.ExistsInRemote` SHALL return `true` (disk cache hit on fast path) and the blob SHALL NOT be re-uploaded

#### Scenario: Unchanged directory across runs (slow path)
- **WHEN** directory `documents/` has identical files between two archive runs but on a different machine
- **THEN** `FileTreeService.ExistsInRemote` SHALL return `true` (empty marker file on disk from slow-path prefetch) and the blob SHALL NOT be re-uploaded

#### Scenario: Empty directory skipped
- **WHEN** a directory contains no files (directly or in subdirectories)
- **THEN** no tree blob SHALL be created for that directory

#### Scenario: Tree hash computation
- **WHEN** a tree blob is serialized to text
- **THEN** the tree hash SHALL be SHA256 of the UTF-8 encoded text bytes, optionally passphrase-seeded via `IEncryptionService.ComputeHash`

### Requirement: Snapshot creation
The system SHALL create a snapshot manifest after both chunk-index flush and tree construction complete. The snapshot SHALL be stored at `snapshots/<UTC-timestamp>` (e.g., `2026-03-22T150000.000Z`) and SHALL contain the root tree hash, timestamp, file count, total size, and Arius version. Snapshots SHALL NEVER be deleted. `SnapshotService.CreateAsync` is responsible for both uploading the snapshot manifest to Azure and writing the full JSON manifest to `~/.arius/{repo}/snapshots/<timestamp>` on disk (write-through), enabling fast-path cache validation on subsequent runs.

After uploads complete and archive cache coordination has invalidated stale mutable chunk-index cache state when needed, the archive pipeline MAY run `ChunkIndexService.FlushAsync` and `FileTreeBuilder.SynchronizeAsync` concurrently. Snapshot creation SHALL wait for both operations to complete successfully.

#### Scenario: Successful snapshot creation
- **WHEN** the archive pipeline completes successfully
- **THEN** a snapshot manifest SHALL be uploaded to `snapshots/<timestamp>` with the root tree hash and metadata

#### Scenario: Snapshot reflects current state
- **WHEN** files have been added, modified, and deleted since the last archive
- **THEN** the new snapshot SHALL only contain files that exist at archive time (no deleted files)

#### Scenario: Snapshot JSON written to disk after archive
- **WHEN** the archive pipeline creates snapshot `2026-03-22T150000.000Z`
- **THEN** `SnapshotService.CreateAsync` SHALL write the full JSON manifest to `~/.arius/{repo}/snapshots/2026-03-22T150000.000Z`

#### Scenario: Cache coordination runs before end-of-pipeline work
- **WHEN** the archive pipeline begins the end-of-pipeline phase
- **THEN** filetree cache validation/materialization and chunk-index cache invalidation SHALL have been coordinated before any tree existence checks or chunk-index flush operations

#### Scenario: Archive invalidates chunk index from filetree validation result
- **WHEN** `FileTreeService.ValidateAsync` returns `FileTreeValidationResult` with `SnapshotMismatch` set
- **THEN** `ArchiveCommandHandler` SHALL ask `ChunkIndexService` to invalidate chunk-index caches before chunk-index flush or tree existence checks

#### Scenario: Flush and tree synchronization may run concurrently
- **WHEN** chunk uploads have completed and archive cache coordination has completed
- **THEN** the archive pipeline MAY run chunk-index flush and filetree synchronization concurrently
- **AND** it SHALL NOT publish a snapshot until both have completed successfully

### Requirement: Crash-recoverable archive
The archive pipeline SHALL use optimistic concurrency for all chunk uploads: uploads are attempted unconditionally without a pre-flight HEAD check.

`OpenWriteAsync` and `UploadAsync(overwrite:false)` use create-if-not-exists semantics (IfNoneMatch=*). If the blob already exists, `BlobAlreadyExistsException` is raised.

On catching `BlobAlreadyExistsException`, the pipeline SHALL perform a HEAD check (GetMetadataAsync) to determine blob completeness using the `arius_type` metadata sentinel:
- `arius_type` present → blob is fully committed (body + metadata); recover ContentLength or metadata as compressedSize and continue without re-uploading
- `arius_type` absent → blob body was committed but metadata was not yet written (partial state); delete the blob and retry the upload from scratch (goto retry)

This pattern applies to all three upload sub-stages: large file upload (Stage 4a), tar blob upload (Stage 4c-tar), and thin chunk creation (Stage 4c-thin). Thin chunk creation SHALL upload an empty blob body and then set all required thin chunk metadata, including `arius_type: thin`, `parent_chunk_hash`, `original_size`, and `compressed_size`, in one metadata update. Chunk-index flush interruption SHALL be recoverable by rerunning archive or by running explicit full chunk-index repair; a failed run SHALL NOT publish a snapshot that references unflushed chunk-index entries.

#### Scenario: Re-run after crash - fully committed blob
- **WHEN** a crash-recovery re-run encounters a fully committed blob (BlobAlreadyExistsException + `arius_type` present)
- **THEN** the pipeline SHALL recover compressedSize from ContentLength or metadata and continue without re-uploading

#### Scenario: Re-run after crash - partially committed blob
- **WHEN** a crash-recovery re-run encounters a partially committed blob (BlobAlreadyExistsException + `arius_type` absent)
- **THEN** the pipeline SHALL delete the blob and retry the upload

#### Scenario: Thin chunk already complete
- **WHEN** `UploadAsync(overwrite:false)` raises BlobAlreadyExistsException for a thin chunk and `arius_type` is present
- **AND** required thin chunk metadata is present and valid
- **THEN** the pipeline SHALL skip silently (fully complete)

#### Scenario: Thin chunk partially committed
- **WHEN** `UploadAsync(overwrite:false)` raises BlobAlreadyExistsException for a thin chunk and `arius_type` is absent
- **THEN** the pipeline SHALL delete the thin blob and retry

#### Scenario: Clean run (no crash)
- **WHEN** no crash occurred (normal run)
- **THEN** dedup filters known hashes so upload stages do not encounter existing blobs for indexed content; BlobAlreadyExistsException remains a recovery path for index misses and concurrent/partial prior runs

#### Scenario: Chunk-index flush interrupted before snapshot
- **WHEN** archive fails after uploading chunks but before all touched chunk-index shards are flushed
- **THEN** the failed run SHALL NOT publish a new snapshot
- **AND** a rerun or explicit full chunk-index repair SHALL be able to restore complete chunk-index coverage for the uploaded chunks
