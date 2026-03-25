## MODIFIED Requirements

### Requirement: Container layout
The blob storage SHALL organize blobs into the following virtual directories: `chunks/` (configurable tier), `chunks-rehydrated/` (Hot tier, temporary), `filetrees/` (Cool tier), `snapshots/` (Cool tier), `chunk-index/` (Cool tier).

Filetree blobs SHALL use content type `text/plain; charset=utf-8`.

#### Scenario: Chunk stored in correct path
- **WHEN** a large file with hash `abc123` is uploaded
- **THEN** the blob SHALL be at `chunks/abc123`

#### Scenario: Rehydrated chunk path
- **WHEN** a chunk is rehydrated for restore
- **THEN** the rehydrated copy SHALL be at `chunks-rehydrated/<chunk-hash>`

#### Scenario: Tree blob path and content type
- **WHEN** a tree blob with hash `def456` is uploaded
- **THEN** the blob SHALL be at `filetrees/def456` with content type `text/plain; charset=utf-8`

### Requirement: Tiered chunk index cache
The system SHALL implement a three-tier cache for chunk index shards: L1 in-memory LRU (configurable size via `--dedup-cache-mb`, default 512 MB), L2 local disk cache at `~/.arius/{accountName}-{containerName}/chunk-index/`, L3 remote Azure Blob. On miss at L1, the shard SHALL be loaded from L2. On miss at L2, the shard SHALL be downloaded from L3 and saved to L2. Shards promoted to L1 SHALL evict the least-recently-used shard when the memory budget is exceeded.

Shard entries SHALL use a field-count convention: 3 space-separated fields for large files (`<content-hash> <original-size> <compressed-size>`), 4 space-separated fields for small files (`<content-hash> <chunk-hash> <original-size> <compressed-size>`). This format SHALL be consistent across L2 and L3 (before L3 compression/encryption).

#### Scenario: L1 cache hit
- **WHEN** a shard was recently accessed and is in the in-memory LRU
- **THEN** the lookup SHALL return immediately without disk or network I/O

#### Scenario: L2 cache hit
- **WHEN** a shard is not in L1 but was previously downloaded to disk
- **THEN** the shard SHALL be loaded from disk, promoted to L1, and returned

#### Scenario: L3 cache miss
- **WHEN** a shard has never been accessed (first archive on a new machine)
- **THEN** the shard SHALL be downloaded from Azure, saved to L2, promoted to L1, and returned

#### Scenario: New shard (404)
- **WHEN** a shard does not exist in Azure (new prefix, first archive)
- **THEN** an empty shard SHALL be created in L1
