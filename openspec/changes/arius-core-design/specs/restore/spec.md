## ADDED Requirements

### Requirement: Restore a single file
The system SHALL restore a single file from a snapshot to the local filesystem.

#### Scenario: Restore a single large file
- **WHEN** the user requests restore of a file whose content hash maps directly to a solo chunk
- **THEN** the system SHALL locate the chunk, rehydrate if needed, download, decrypt, decompress, and write the file to the local path with original metadata (created, modified dates)

#### Scenario: Restore a single small file from tar bundle
- **WHEN** the user requests restore of a file whose content hash requires chunk index lookup
- **THEN** the system SHALL look up the tar-chunk-hash in the chunk index, rehydrate the tar chunk if needed, download, decrypt, decompress, extract the target file from the tar, and write it to the local path

### Requirement: Restore multiple files
The system SHALL restore multiple specified files from a snapshot.

#### Scenario: Restore multiple files sharing a tar bundle
- **WHEN** multiple requested files reside in the same tar bundle
- **THEN** the system SHALL download and cache the tar bundle locally and extract all requested files from the cached copy, avoiding redundant downloads

### Requirement: Restore a directory
The system SHALL restore a directory and all files underneath it from a snapshot.

#### Scenario: Restore entire directory tree
- **WHEN** the user requests restore of a directory path
- **THEN** the system SHALL traverse the snapshot tree to enumerate all files under that path and restore each file to its original relative path under the target location

### Requirement: Restore a full snapshot
The system SHALL restore all files from a snapshot.

#### Scenario: Restore full snapshot to local directory
- **WHEN** the user requests a full snapshot restore
- **THEN** the system SHALL traverse the entire snapshot tree and restore every file to its original relative path under the target location

### Requirement: Snapshot version selection
The system SHALL allow specifying which snapshot version to restore from, defaulting to the latest.

#### Scenario: Restore from latest snapshot (default)
- **WHEN** the user runs `arius restore` without `-v`
- **THEN** the system SHALL use the most recent snapshot (lexicographically last snapshot name)

#### Scenario: Restore from specific snapshot
- **WHEN** the user specifies `-v <snapshot-name>`
- **THEN** the system SHALL restore from that exact snapshot

### Requirement: Rehydration from archive tier
Chunks in Archive tier SHALL NOT be rehydrated in place. The system SHALL copy them to `chunks-rehydrated/` for download.

#### Scenario: Chunk needs rehydration
- **WHEN** a chunk is in Archive tier and not already present in `chunks-rehydrated/`
- **THEN** the system SHALL submit a rehydration copy request to `chunks-rehydrated/<hash>` and poll until ready

#### Scenario: Chunk already rehydrated
- **WHEN** a chunk is already present in `chunks-rehydrated/`
- **THEN** the system SHALL download it immediately without submitting a new rehydration request

### Requirement: Two-phase restore processing
The system SHALL process already-rehydrated chunks immediately while waiting for remaining rehydrations.

#### Scenario: Mixed rehydration state
- **WHEN** some requested chunks are already rehydrated and others are not
- **THEN** the system SHALL immediately begin downloading and restoring files from rehydrated chunks, while concurrently polling for the remaining chunks to become available

### Requirement: Local chunk cache
The system SHALL maintain a local cache of downloaded chunks during restore to avoid re-downloading.

#### Scenario: Same tar bundle needed by multiple files
- **WHEN** multiple files being restored are in the same tar bundle
- **THEN** the system SHALL download the tar bundle once to local cache and extract each file from the cache

#### Scenario: Cleanup after restore
- **WHEN** a full restore operation completes
- **THEN** the system SHALL delete the local chunk cache and the `chunks-rehydrated/` blobs from blob storage

### Requirement: Pointer file creation on restore
The system SHALL create pointer files for all restored files.

#### Scenario: Pointer files created during restore
- **WHEN** a file is successfully restored to disk
- **THEN** the system SHALL create a corresponding `.pointer.arius` file containing the content hash

### Requirement: Metadata restoration
The system SHALL restore file metadata from the snapshot.

#### Scenario: File dates restored
- **WHEN** a file is restored
- **THEN** the system SHALL set the file's created and modified timestamps to the values recorded in the snapshot tree node
