# Restore Pipeline Spec

## Purpose

Defines the restore pipeline for Arius: snapshot resolution, tree traversal, conflict checking, chunk resolution, rehydration, cost estimation, streaming restore, and pointer file creation.

## Requirements

### Requirement: Snapshot resolution
The system SHALL resolve the target snapshot for restore operations. When `-v` is specified, the system SHALL locate the exact snapshot. When `-v` is omitted, the system SHALL use the latest snapshot. The system SHALL download and parse the snapshot manifest to obtain the root tree hash.

#### Scenario: Restore latest snapshot
- **WHEN** `arius restore /photos/` is run without `-v`
- **THEN** the system SHALL list all snapshots and select the most recent one

#### Scenario: Restore specific version
- **WHEN** `arius restore /photos/ -v 2026-03-21T140000.000Z` is run
- **THEN** the system SHALL locate and use the specified snapshot

#### Scenario: Snapshot not found
- **WHEN** a specified snapshot version does not exist
- **THEN** the system SHALL report an error and exit

### Requirement: Tree traversal to target path
The system SHALL walk the merkle tree from the root to the requested path, downloading tree blobs on demand through the tiered cache (L1 memory → L2 disk → L3 Azure). Tree blobs SHALL be parsed as text format: each line is either `<hash> F <created> <modified> <name>` (file entry) or `<hash> D <name>` (directory entry). For `F` lines, the system SHALL split on the first 4 spaces to extract hash, type, created, modified, and name (remainder). For `D` lines, the system SHALL split on the first 2 spaces to extract hash, type, and name (remainder). For a file restore, traversal stops at the file's directory. For a directory restore, the system SHALL enumerate the full subtree. For a full restore, the entire tree SHALL be traversed.

#### Scenario: Restore single file
- **WHEN** restoring `photos/2024/june/vacation.jpg`
- **THEN** the system SHALL download tree blobs for `/`, `photos/`, `photos/2024/`, `photos/2024/june/` and locate the file entry

#### Scenario: Restore directory
- **WHEN** restoring `photos/2024/`
- **THEN** the system SHALL traverse the full subtree under `photos/2024/` and collect all file entries

#### Scenario: Restore full snapshot
- **WHEN** restoring with `--full`
- **THEN** the system SHALL traverse the entire tree and collect all file entries

#### Scenario: Parse file entry from tree blob
- **WHEN** a tree blob line is `abc123... F 2026-03-25T10:00:00.0000000+00:00 2026-03-25T12:30:00.0000000+00:00 my vacation photo.jpg`
- **THEN** the system SHALL parse hash as `abc123...`, type as file, created and modified as the ISO-8601 timestamps, and name as `my vacation photo.jpg`

#### Scenario: Parse directory entry from tree blob
- **WHEN** a tree blob line is `def456... D 2024 trip/`
- **THEN** the system SHALL parse hash as `def456...`, type as directory, and name as `2024 trip/`

### Requirement: Restore conflict check
The system SHALL check each target file against the local filesystem before restoring. If a local file exists and its hash matches the snapshot entry, it SHALL be skipped. If a local file exists with a different hash, the system SHALL prompt the user (overwrite? y/N/all). This check makes restore idempotent on re-run.

#### Scenario: Local file matches snapshot
- **WHEN** local file `photos/vacation.jpg` exists and its hash matches the snapshot entry
- **THEN** the system SHALL skip the file (already restored correctly)

#### Scenario: Local file differs from snapshot
- **WHEN** local file `photos/vacation.jpg` exists but its hash differs from the snapshot entry
- **THEN** the system SHALL prompt the user whether to overwrite

#### Scenario: Local file does not exist
- **WHEN** the target path has no local file
- **THEN** the system SHALL proceed with restore

### Requirement: Chunk resolution from index
The system SHALL look up each content hash in the chunk index to determine the chunk hash and chunk type. For large files, the content-hash equals the chunk-hash (reconstructed on parse from 3-field entries). For tar-bundled files, the content-hash maps to a different chunk-hash (the tar, from 4-field entries). The system SHALL group all file entries by chunk hash to minimize downloads (multiple files from the same tar → one download).

#### Scenario: Large file chunk resolution
- **WHEN** content hash `abc123` is looked up and the in-memory entry has content-hash equal to chunk-hash
- **THEN** the system SHALL identify this as a large file chunk for direct streaming restore

#### Scenario: Tar-bundled file chunk resolution
- **WHEN** content hash `def456` is looked up and the in-memory entry has a different chunk-hash `tar789`
- **THEN** the system SHALL identify this as a tar-bundled file and group it with other files from `tar789`

#### Scenario: Multiple files from same tar
- **WHEN** 5 files all map to the same tar chunk hash
- **THEN** the system SHALL download the tar once and extract all 5 files in a single pass

### Requirement: Rehydration status check
The system SHALL check the availability of each needed chunk before downloading. It SHALL check: (1) `chunks-rehydrated/<chunk-hash>` exists → already rehydrated, (2) `chunks/<chunk-hash>` blob tier is Hot/Cool → directly downloadable, (3) blob tier is Archive → needs rehydration. The result SHALL categorize chunks into: ready (direct download), rehydrated (download from rehydrated copy), and needs-rehydration.

#### Scenario: Chunk already rehydrated from previous run
- **WHEN** `chunks-rehydrated/<chunk-hash>` exists from a previous restore attempt
- **THEN** the system SHALL download from `chunks-rehydrated/` without starting a new rehydration

#### Scenario: Chunk in Hot tier
- **WHEN** a chunk was uploaded with `--tier Hot`
- **THEN** the system SHALL download directly from `chunks/` without rehydration

#### Scenario: Chunk in Archive tier
- **WHEN** a chunk is in Archive tier with no rehydrated copy
- **THEN** the system SHALL mark it as needing rehydration

### Requirement: Cost estimation and user confirmation
The system SHALL display a cost breakdown before restoring and require user confirmation. The breakdown SHALL include: files to restore, chunks needed (categorized by availability), estimated rehydration cost (Standard and High Priority), and estimated download/egress cost. The chunk index entry's `compressed-size` field SHALL be used for cost calculations.

#### Scenario: Cost estimation display
- **WHEN** a restore is requested
- **THEN** the system SHALL display chunk counts, sizes, and estimated costs before prompting for confirmation

#### Scenario: User declines
- **WHEN** the user responds "N" to the confirmation prompt
- **THEN** the system SHALL exit without restoring or rehydrating

#### Scenario: Rehydration priority selection
- **WHEN** archive-tier chunks need rehydration
- **THEN** the system SHALL prompt the user to choose Standard or High Priority and display the cost difference

### Requirement: Streaming restore (no local cache)
The system SHALL restore files by streaming chunks directly to their final path without intermediate local caching. For large files: download → decrypt → gunzip → write to final path. For tar bundles: download → decrypt → gunzip → iterate tar entries → extract needed files by content-hash name → write to final paths. Peak temp disk usage SHALL be zero (fully streaming). All files needing a given chunk SHALL be grouped and extracted in a single streaming pass.

#### Scenario: Large file streaming restore
- **WHEN** restoring a 2 GB large file
- **THEN** the system SHALL stream download → decrypt → gunzip → write directly to the target path with no intermediate temp file

#### Scenario: Tar bundle streaming extract
- **WHEN** restoring 3 files that are bundled in the same tar
- **THEN** the system SHALL download the tar once, stream through the tar entries, and extract the 3 matching content-hash entries to their final paths

#### Scenario: Tar entry not needed
- **WHEN** a tar contains 300 files but only 2 are needed for this restore
- **THEN** the system SHALL skip the 298 unneeded entries during streaming tar iteration

### Requirement: Rehydration kick-off
The system SHALL start rehydration for all archive-tier chunks that are not yet rehydrated, using the user-selected priority (Standard or High). Rehydration SHALL copy blobs to `chunks-rehydrated/` (Hot tier). If Azure throttles the request, the system SHALL retry with exponential backoff. After starting rehydration, the system SHALL exit with a message indicating how many chunks are pending and suggesting the user re-run later.

#### Scenario: Start rehydration
- **WHEN** 62 chunks need rehydration with Standard priority
- **THEN** the system SHALL issue copy-to-rehydrate requests for all 62 and report "Re-run this command in ~15 hours to complete the restore"

#### Scenario: Azure throttling during rehydration
- **WHEN** Azure returns a throttling error during rehydration requests
- **THEN** the system SHALL retry with exponential backoff

#### Scenario: Rehydration already pending
- **WHEN** a chunk already has a pending rehydration request from a previous run
- **THEN** the system SHALL recognize the pending state and not issue a duplicate request

### Requirement: Idempotent restore
Restore SHALL be fully idempotent. Re-running the same restore command SHALL: skip files already restored correctly (hash match), download newly rehydrated chunks, re-request rehydration for still-pending chunks, and report remaining files. Each run is a self-contained scan-and-act cycle with no persistent local state.

#### Scenario: Partial restore re-run
- **WHEN** a restore previously restored 500 of 1000 files and rehydration has completed for 300 more chunks
- **THEN** re-running SHALL skip the 500 completed files, restore the 300 newly available, and report 200 still pending

#### Scenario: Full restore complete
- **WHEN** all files have been restored across multiple runs
- **THEN** the system SHALL report all files restored and prompt to clean up `chunks-rehydrated/`

### Requirement: Cleanup of rehydrated blobs
After a full restore is complete, the system SHALL prompt the user to delete blobs in `chunks-rehydrated/`. These are Hot-tier copies that incur ongoing storage costs.

#### Scenario: Cleanup prompt after full restore
- **WHEN** all requested files are restored
- **THEN** the system SHALL prompt "Delete N rehydrated chunks (X GB) from Azure? [Y/n]"

#### Scenario: User declines cleanup
- **WHEN** user responds "n" to the cleanup prompt
- **THEN** the blobs SHALL be retained (useful if another restore is planned soon)

### Requirement: Pointer file creation during restore
The system SHALL create `.pointer.arius` files alongside each restored file (unless `--no-pointers` is set). The pointer SHALL contain the content hash. File metadata (created, modified dates) SHALL be set from the tree blob entry.

#### Scenario: Restored file gets pointer
- **WHEN** `photos/vacation.jpg` is restored from a snapshot
- **THEN** `photos/vacation.jpg.pointer.arius` SHALL be created with the content hash, and the file's modified date SHALL be set from the snapshot metadata
