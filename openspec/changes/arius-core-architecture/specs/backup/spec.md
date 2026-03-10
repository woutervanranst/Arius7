## ADDED Requirements

### Requirement: Create snapshot from filesystem paths
The system SHALL scan specified filesystem paths, chunk files using the configured chunker, deduplicate against the existing index, pack new blobs, encrypt, upload to Azure, and create a snapshot referencing the root tree.

#### Scenario: Backup new files
- **WHEN** user runs `backup /path/to/data`
- **THEN** the system scans the path, chunks each file, uploads new blobs in packs to archive tier, writes tree blobs to cold tier, writes a new index delta to cold tier, and writes a snapshot to cold tier

#### Scenario: Backup with full deduplication
- **WHEN** a file's chunks all exist in the index (from a previous backup)
- **THEN** no new pack data is uploaded; only tree and snapshot metadata are created

### Requirement: Incremental backup via metadata comparison
The system SHALL compare file metadata (mtime, size) against the previous snapshot to skip unchanged files without re-reading their content.

#### Scenario: Unchanged file skipped
- **WHEN** a file's mtime and size match the previous snapshot
- **THEN** the file is not re-read or re-chunked; its existing content hashes are reused

#### Scenario: Modified file re-chunked
- **WHEN** a file's mtime or size differs from the previous snapshot
- **THEN** the file is re-chunked and its blobs are dedup-checked against the index

### Requirement: Exclude patterns
The system SHALL support `--exclude` patterns to skip files and directories matching glob patterns.

#### Scenario: Exclude by pattern
- **WHEN** user runs `backup --exclude "*.tmp" --exclude ".cache/" /data`
- **THEN** files matching `*.tmp` and directories named `.cache/` are excluded from the backup

### Requirement: Tagging
The system SHALL support `--tag` to attach tags to the created snapshot.

#### Scenario: Backup with tags
- **WHEN** user runs `backup --tag daily --tag automated /data`
- **THEN** the snapshot is created with tags `["daily", "automated"]`

### Requirement: Host and path metadata
Each snapshot SHALL record the hostname, username, and backed-up paths.

#### Scenario: Snapshot metadata
- **WHEN** a backup completes
- **THEN** the snapshot contains the originating hostname, username, timestamp, and list of backed-up paths

### Requirement: Backup progress streaming
The backup operation SHALL stream progress events including: files scanned, files new, files unchanged, bytes processed, bytes uploaded, packs created.

#### Scenario: Progress during backup
- **WHEN** a backup is in progress
- **THEN** the handler yields `IAsyncEnumerable<BackupEvent>` including scan progress, chunk progress, upload progress, and completion summary

### Requirement: Parent snapshot reference
A new snapshot SHALL reference the previous snapshot (if any) for the same paths and host as its parent, enabling incremental change detection.

#### Scenario: Parent linkage
- **WHEN** a backup creates a snapshot for paths that were previously backed up from the same host
- **THEN** the new snapshot's `parent` field references the previous snapshot's ID
