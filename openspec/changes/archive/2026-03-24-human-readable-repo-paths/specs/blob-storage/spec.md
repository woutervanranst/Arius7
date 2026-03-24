## MODIFIED Requirements

### Requirement: Per-repository cache identification
The local cache SHALL be organized by repository using a human-readable directory name: `{accountName}-{containerName}`. The cache path SHALL be `~/.arius/{accountName}-{containerName}/`. Azure account names (`[a-z0-9]{3,24}`) do not contain hyphens, so the first hyphen in the directory name unambiguously separates account from container. The `ComputeRepoId` hash function SHALL be removed.

#### Scenario: Two repositories on same machine
- **WHEN** archiving to account `mystorageacct` container `photos` and account `otherstorage` container `backups`
- **THEN** the cache directories SHALL be `~/.arius/mystorageacct-photos/` and `~/.arius/otherstorage-backups/`

#### Scenario: Directory name is human-readable
- **WHEN** listing `~/.arius/`
- **THEN** each subdirectory name SHALL clearly identify the account and container it belongs to

#### Scenario: Account with multiple containers
- **WHEN** archiving to account `myacct` with containers `photos` and `documents`
- **THEN** the cache directories SHALL be `~/.arius/myacct-photos/` and `~/.arius/myacct-documents/`

### Requirement: Tiered chunk index cache
The system SHALL implement a three-tier cache for chunk index shards: L1 in-memory LRU (configurable size via `--dedup-cache-mb`, default 512 MB), L2 local disk cache at `~/.arius/{accountName}-{containerName}/chunk-index/`, L3 remote Azure Blob. On miss at L1, the shard SHALL be loaded from L2. On miss at L2, the shard SHALL be downloaded from L3 and saved to L2. Shards promoted to L1 SHALL evict the least-recently-used shard when the memory budget is exceeded.

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

### Requirement: Tree blob caching
Tree blobs SHALL be cached on local disk at `~/.arius/{accountName}-{containerName}/filetrees/` and valid indefinitely (content-addressed = immutable). The cache SHALL be used for `ls` and `restore` tree traversal.

#### Scenario: Cached tree blob reuse
- **WHEN** a tree blob was downloaded during a previous `ls` or `restore`
- **THEN** subsequent operations SHALL use the cached version without contacting Azure

#### Scenario: Cache on mounted volume in Docker
- **WHEN** running in Docker
- **THEN** the cache directory (`~/.arius/`) SHALL be on a mounted volume to persist across container restarts
