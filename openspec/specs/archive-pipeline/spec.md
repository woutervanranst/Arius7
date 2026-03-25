# Archive Pipeline Spec

## Purpose

Defines the archive pipeline for Arius: file enumeration, hashing, deduplication, size-based routing, large file upload, tar bundling, thin chunk creation, index shard management, Merkle tree construction, snapshot creation, pointer files, and crash recovery.

## Requirements

### Requirement: File enumeration
The system SHALL recursively enumerate all files in the local root directory, producing FilePair units for archiving. Files with the `.pointer.arius` suffix SHALL always be treated as pointer files. All other files SHALL be treated as binary files. If a file cannot be read (e.g., system-protected), the system SHALL log a warning and continue with the remaining files. Enumeration SHALL be depth-first to provide directory affinity for the tar builder.

#### Scenario: Binary file with matching pointer
- **WHEN** a binary file `photos/vacation.jpg` exists alongside `photos/vacation.jpg.pointer.arius`
- **THEN** the system SHALL produce a FilePair with both binary and pointer present

#### Scenario: Binary file without pointer
- **WHEN** a binary file `documents/report.pdf` exists with no corresponding `.pointer.arius` file
- **THEN** the system SHALL produce a FilePair with binary present and pointer absent

#### Scenario: Pointer file without binary (thin archive)
- **WHEN** a pointer file `music/song.mp3.pointer.arius` exists with no corresponding binary
- **THEN** the system SHALL produce a FilePair with pointer present and binary absent, using the hash from the pointer file

#### Scenario: Inaccessible file
- **WHEN** a file cannot be read due to permissions or system protection
- **THEN** the system SHALL log a warning with the file path and reason, skip the file, and continue enumeration

#### Scenario: Pointer file with invalid content
- **WHEN** a `.pointer.arius` file contains content that is not a valid hex hash
- **THEN** the system SHALL log a warning and treat the file as having no valid pointer

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
The system SHALL check each content hash against the chunk index before uploading. The dedup stage SHALL batch hashes by their 2-byte prefix and resolve them through the tiered cache (L1 memory LRU → L2 disk → L3 Azure). An in-flight set SHALL prevent duplicate uploads of the same content hash within a single archive run. The dedup stage SHALL be single-threaded to manage the in-flight set without locking.

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

### Requirement: Size-based routing
The system SHALL route files to the large file pipeline if their size is >= `--small-file-threshold` (default 1 MB) and to the tar builder if their size is < the threshold.

#### Scenario: File at threshold boundary
- **WHEN** a file is exactly 1,048,576 bytes (1 MB, the default threshold)
- **THEN** the system SHALL route it to the large file pipeline

#### Scenario: Small file routing
- **WHEN** a file is 500 bytes
- **THEN** the system SHALL route it to the tar builder

### Requirement: Large file upload
The system SHALL upload large files individually as chunks. The upload pipeline SHALL stream: read file → gzip compress → encrypt (if passphrase) → upload to `chunks/<content-hash>`. The blob metadata SHALL include `arius-type: large`, `arius-complete: true` (set after body upload), `original-size`, and `chunk-size`. Content type SHALL be set to `application/aes256cbc+gzip` (encrypted) or `application/gzip` (plaintext). Multiple large files SHALL upload concurrently (N workers, network-bound).

#### Scenario: Large file upload with encryption
- **WHEN** a 50 MB file is uploaded with a passphrase
- **THEN** the system SHALL stream the file through gzip → AES-256-CBC encryption → upload to `chunks/<content-hash>` with `arius-type: large` and content type `application/aes256cbc+gzip`

#### Scenario: Large file upload without encryption
- **WHEN** a 50 MB file is uploaded without a passphrase
- **THEN** the system SHALL stream the file through gzip → upload to `chunks/<content-hash>` with content type `application/gzip`

#### Scenario: Upload completes successfully
- **WHEN** a chunk upload finishes
- **THEN** the blob metadata SHALL have `arius-complete: true` set

### Requirement: Tar builder
The system SHALL bundle small files into tar archives using a single tar builder. Files inside the tar SHALL be named by their content-hash (not original path). The tar builder SHALL seal and hand off the tar to the upload channel when the accumulated uncompressed size reaches `--tar-target-size` (default 64 MB). After sealing, the builder SHALL immediately start a new tar. The tar builder SHALL stream to a temp file on disk (not memory). Depth-first enumeration provides natural directory affinity.

#### Scenario: Tar sealing at target size
- **WHEN** accumulated small files in the current tar reach 64 MB uncompressed
- **THEN** the system SHALL seal the tar, compute its tar-hash, and hand it off for upload

#### Scenario: Partial tar at end of archive
- **WHEN** the archive run completes with a partially filled tar (< 64 MB)
- **THEN** the system SHALL seal and upload the partial tar

#### Scenario: Tar with single file
- **WHEN** only one small file needs archiving
- **THEN** the system SHALL create a tar with that single file entry

#### Scenario: Tar upload format
- **WHEN** a sealed tar is uploaded
- **THEN** the blob SHALL be stored at `chunks/<tar-hash>` with `arius-type: tar` and content type `application/aes256cbc+tar+gzip` (or `application/tar+gzip` without passphrase)

### Requirement: Thin chunk creation
The system SHALL create a thin chunk blob for each small file after its tar is successfully uploaded. The thin chunk SHALL be stored at `chunks/<content-hash>` with body containing the tar-hash (plain text). Blob metadata SHALL include `arius-type: thin`, `arius-complete: true`, `original-size`, and `compressed-size` (proportional estimate based on the tar's compression ratio).

#### Scenario: Thin chunk for tar-bundled file
- **WHEN** file with content-hash `abc123` is bundled in tar with hash `def456`
- **THEN** a blob SHALL be created at `chunks/abc123` with body `def456`, `arius-type: thin`, and `arius-complete: true`

#### Scenario: Thin chunk enables crash recovery
- **WHEN** archive crashes after tar upload but before index update, and the archive is re-run
- **THEN** the system SHALL discover existing thin chunks via HEAD, read the tar-hash from the body, and recover the index mapping without re-uploading

### Requirement: Index shard merge and upload
The system SHALL collect all new index entries (content-hash → chunk-hash, original-size, compressed-size) and upload updated chunk index shards once at the end of the archive run. For each modified shard prefix, the system SHALL download the existing shard (if cached or from Azure), merge new entries, and upload.

The L3 wire format (Azure blobs) SHALL be: plaintext lines → gzip compressed → optionally AES-256-CBC encrypted. Content type SHALL be `application/aes256cbc+gzip` (encrypted) or `application/gzip` (plaintext).

The L2 local disk cache SHALL store shards as **plaintext lines only** — no gzip compression, no encryption. This makes the local cache human-readable and avoids unnecessary CPU overhead on every cache read/write. The L2 format SHALL NOT change based on whether a passphrase is provided.

The shard entry format SHALL be: `<content-hash> <chunk-hash> <original-size> <compressed-size>\n`.

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
The system SHALL build a content-addressed merkle tree of directories after all uploads complete. Tree construction SHALL use a two-phase approach: (1) write completed file entries to an unsorted manifest temp file during the pipeline, (2) external sort by path, then stream through building tree blobs bottom-up one directory at a time. Each tree blob SHALL be JSON containing entries with name, type, hash, created, and modified. File size SHALL NOT be stored in tree blobs (it is in the chunk index). Empty directories SHALL be skipped. Unchanged tree blobs SHALL be deduplicated by content hash.

#### Scenario: Directory with files produces tree blob
- **WHEN** directory `photos/2024/june/` contains files `a.jpg` and `b.jpg`
- **THEN** a tree blob SHALL be created with 2 file entries and uploaded to `filetrees/<tree-hash>`

#### Scenario: Unchanged directory across runs
- **WHEN** directory `documents/` has identical files and metadata between two archive runs
- **THEN** the tree blob hash SHALL be identical and the blob SHALL NOT be re-uploaded

#### Scenario: Empty directory skipped
- **WHEN** a directory contains no files (directly or in subdirectories)
- **THEN** no tree blob SHALL be created for that directory

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
Archive runs SHALL be idempotent on re-run after a crash. Each uploaded chunk blob SHALL carry `arius-complete` metadata set after the body upload completes. On re-run, the system SHALL HEAD-check each content-hash before uploading: 200 + `arius-complete` → skip and recover index entry; 200 without `arius-complete` → overwrite (incomplete upload); 404 → upload fresh. No explicit crash detection marker is needed — the HEAD check path is always safe and cheap.

#### Scenario: Re-run after crash during uploads
- **WHEN** archive crashes after uploading 50 of 100 chunks and is re-run
- **THEN** the system SHALL detect the 50 completed chunks via HEAD + `arius-complete`, skip their upload, recover their index entries, and upload only the remaining 50

#### Scenario: Re-run after crash during thin chunk creation
- **WHEN** archive crashes after uploading a tar but before creating all thin chunks
- **THEN** files with existing thin chunks SHALL be recovered; files without thin chunks SHALL be re-bundled into a new tar

#### Scenario: Clean re-run (no crash)
- **WHEN** archive is re-run on unchanged files with no prior crash
- **THEN** the HEAD checks SHALL return 404 (files not yet in this run) or dedup check SHALL find them in the index; no performance regression from crash recovery logic

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

### Requirement: Concurrent pipeline with bounded memory
The archive pipeline SHALL use bounded Channel<T> between stages. Memory consumption SHALL be constant (~30 MB) regardless of file count. The on-disk manifest for tree building SHALL use external sort. Channel backpressure SHALL automatically throttle upstream stages when downstream stages are slow.

#### Scenario: 500M file archive memory bound
- **WHEN** archiving 500 million 2 KB files
- **THEN** peak memory usage SHALL remain bounded (not proportional to file count)
