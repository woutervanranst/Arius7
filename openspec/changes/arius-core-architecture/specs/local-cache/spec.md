## ADDED Requirements

### Requirement: Local SQLite cache
The system SHALL maintain an optional local SQLite database at `~/.arius/cache/{repo-id}.db` containing cached copies of index, snapshot, and tree data for fast local queries.

#### Scenario: Cache creation on first connect
- **WHEN** a user connects to a repository with no existing local cache
- **THEN** the system downloads all index, snapshot, and tree blobs from cold tier and builds the SQLite cache

#### Scenario: Cache hit
- **WHEN** a user runs `ls` or `find` and the cache exists
- **THEN** the data is served from the local cache without querying Azure

### Requirement: Delta sync
The cache SHALL track a sync watermark. On subsequent connects, only new blobs (created since the watermark) are downloaded and ingested.

#### Scenario: Incremental sync
- **WHEN** a user connects to a repo that has 5 new index files since the last sync
- **THEN** only those 5 files are downloaded and merged into the cache; the watermark is updated

### Requirement: Cache is fully rebuildable
Deleting the local cache SHALL have no effect on correctness. All operations SHALL produce identical results with or without the cache — the cache only affects performance.

#### Scenario: Delete and rebuild
- **WHEN** a user deletes `~/.arius/cache/{repo-id}.db` and runs any command
- **THEN** the cache is rebuilt from Azure and the command produces correct results

### Requirement: Cache tables
The SQLite cache SHALL contain tables for: blobs (hash → pack ID, offset, length), packs (pack ID → metadata), snapshots (ID → metadata), trees (hash → serialized tree data).

#### Scenario: Blob lookup
- **WHEN** the restore handler needs to find which pack contains a blob
- **THEN** it queries the `blobs` table by SHA-256 hash and receives the pack ID, offset, and length

### Requirement: Operations without cache
All operations SHALL function correctly when no local cache exists, falling back to direct Azure cold-tier reads. The system SHALL offer to build the cache when it would significantly improve performance.

#### Scenario: No-cache operation
- **WHEN** a user runs `snapshots` without a local cache
- **THEN** the system lists blobs from `snapshots/` in Azure, downloads and decrypts each, and displays the results

#### Scenario: Suggest cache build
- **WHEN** a user runs an index-heavy operation (backup, restore) without a cache and the repo has more than 100 index files
- **THEN** the system suggests building a local cache for better performance
