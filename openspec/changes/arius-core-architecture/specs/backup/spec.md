## ADDED Requirements

### Requirement: Create snapshot from filesystem paths via Azure
The system SHALL scan specified filesystem paths, chunk files using the configured chunker, deduplicate against the existing index, pack new blobs, encrypt, upload directly to Azure Blob Storage, and create a snapshot referencing the root tree. No intermediate filesystem write of pack data is required.

#### Scenario: Backup new files
- **WHEN** user runs `backup /path/to/data`
- **THEN** the system scans the path, chunks each file, computes `HMAC-SHA256(master_key, chunk_plaintext)` as each chunk's blob ID, streams sealed packs directly to Azure Blob Storage at the specified data tier, writes tree blobs to cold tier, writes a new index delta to cold tier, and writes a snapshot to cold tier

#### Scenario: Backup with full deduplication
- **WHEN** a file's chunks all exist in the index (from a previous backup)
- **THEN** no new pack data is uploaded; only tree and snapshot metadata are created

### Requirement: Data pack tier selection
The backup command SHALL accept an optional `--tier` parameter to control the Azure Blob Storage access tier for uploaded data packs. If not specified, the Archive tier is used.

#### Scenario: Default tier is Archive
- **WHEN** user runs `backup /path/to/data` without `--tier`
- **THEN** data packs are uploaded to Azure with the Archive access tier

#### Scenario: Explicit Hot tier
- **WHEN** user runs `backup --tier hot /path/to/data`
- **THEN** data packs are uploaded to Azure with the Hot access tier

#### Scenario: Explicit Cool tier
- **WHEN** user runs `backup --tier cool /path/to/data`
- **THEN** data packs are uploaded to Azure with the Cool access tier

#### Scenario: Explicit Cold tier
- **WHEN** user runs `backup --tier cold /path/to/data`
- **THEN** data packs are uploaded to Azure with the Cold access tier

#### Scenario: Tier applies only to data packs
- **WHEN** any tier is specified (or defaulted)
- **THEN** metadata blobs (snapshots, index, trees, keys) are always uploaded with the Cold tier regardless of the `--tier` setting

### Requirement: Blob ID computation uses HMAC-SHA256 with master key
Each chunk's blob ID SHALL be computed as `HMAC-SHA256(master_key, plaintext_chunk_bytes)`. Plain SHA-256 of plaintext SHALL NOT be used as a blob address.

#### Scenario: Blob ID derivation
- **WHEN** a chunk of plaintext bytes is processed
- **THEN** its ID is `hex(HMAC-SHA256(master_key, chunk_bytes))` — opaque without the master key

#### Scenario: Deduplication with HMAC IDs
- **WHEN** the same plaintext chunk appears in two different files or backups
- **THEN** both produce the same HMAC-SHA256 ID (given the same master key) and only one copy is stored

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
