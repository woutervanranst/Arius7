# Blob Storage Spec

## Purpose

Defines the blob storage abstraction layer, container layout, tiered caching, and rehydration strategy for Arius. Arius.Core depends only on the `IBlobStorageService` interface; the Azure implementation lives in Arius.AzureBlob.

## Requirements

### Requirement: Blob storage abstraction
The system SHALL define an `IBlobStorageService` interface in Arius.Core that abstracts all blob storage operations. Arius.Core SHALL NOT reference Azure.Storage.Blobs or any Azure-specific types. The Azure implementation (`Arius.AzureBlob`) SHALL implement this interface.

#### Scenario: Core has no Azure dependency
- **WHEN** Arius.Core is built
- **THEN** it SHALL compile without any reference to Azure.Storage.Blobs

#### Scenario: Alternative backend
- **WHEN** a new storage backend (e.g., S3) is needed in the future
- **THEN** it SHALL be implementable by providing a new `IBlobStorageService` implementation without modifying Core

### Requirement: Chunk blob operations
The `IBlobStorageService` SHALL support: upload blob (streaming, with metadata and tier), download blob (streaming), HEAD check (exists + metadata), list blobs by prefix, set blob metadata, and copy blob (for rehydration). Upload SHALL support setting the access tier (Hot, Cool, Cold, Archive).

#### Scenario: Upload large chunk
- **WHEN** uploading a gzip+encrypted stream to `chunks/<hash>`
- **THEN** the service SHALL upload the stream with specified metadata and tier

#### Scenario: HEAD check for crash recovery
- **WHEN** checking if `chunks/<hash>` exists
- **THEN** the service SHALL return existence, blob metadata (including `arius-complete`), and blob tier

#### Scenario: Download for restore
- **WHEN** downloading `chunks/<hash>` or `chunks-rehydrated/<hash>`
- **THEN** the service SHALL return a readable stream

### Requirement: Chunk types with blob metadata
Each chunk blob SHALL carry metadata distinguishing its type. The `arius-type` metadata field SHALL be one of: `large`, `tar`, `thin`. The `arius-complete` metadata field SHALL be `true` when the upload has finished successfully. Additional metadata: `original-size` (for large and thin), `chunk-size` (compressed blob size), `compressed-size` (for thin: proportional estimate within tar).

#### Scenario: Large chunk metadata
- **WHEN** a large file chunk is uploaded
- **THEN** blob metadata SHALL include `arius-type: large`, `arius-complete: true`, `original-size: <bytes>`, `chunk-size: <bytes>`

#### Scenario: Thin chunk metadata
- **WHEN** a thin chunk is created for a tar-bundled file
- **THEN** blob metadata SHALL include `arius-type: thin`, `arius-complete: true`, `original-size: <bytes>`, `compressed-size: <bytes>`

#### Scenario: Tar chunk metadata
- **WHEN** a tar bundle is uploaded
- **THEN** blob metadata SHALL include `arius-type: tar`, `arius-complete: true`, `chunk-size: <bytes>`

### Requirement: Container layout
The blob storage SHALL organize blobs into the following virtual directories: `chunks/` (configurable tier), `chunks-rehydrated/` (Hot tier, temporary), `filetrees/` (Cool tier), `snapshots/` (Cool tier), `chunk-index/` (Cool tier).

#### Scenario: Chunk stored in correct path
- **WHEN** a large file with hash `abc123` is uploaded
- **THEN** the blob SHALL be at `chunks/abc123`

#### Scenario: Rehydrated chunk path
- **WHEN** a chunk is rehydrated for restore
- **THEN** the rehydrated copy SHALL be at `chunks-rehydrated/<chunk-hash>`

#### Scenario: Tree blob path
- **WHEN** a tree blob with hash `def456` is uploaded
- **THEN** the blob SHALL be at `filetrees/def456`

### Requirement: Tiered chunk index cache
The system SHALL implement a three-tier cache for chunk index shards: L1 in-memory LRU (configurable size via `--dedup-cache-mb`, default 512 MB), L2 local disk cache at `~/.arius/cache/<repo-id>/chunk-index/`, L3 remote Azure Blob. On miss at L1, the shard SHALL be loaded from L2. On miss at L2, the shard SHALL be downloaded from L3 and saved to L2. Shards promoted to L1 SHALL evict the least-recently-used shard when the memory budget is exceeded.

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
Tree blobs SHALL be cached on local disk at `~/.arius/cache/<repo-id>/filetrees/` and valid indefinitely (content-addressed = immutable). The cache SHALL be used for `ls` and `restore` tree traversal.

#### Scenario: Cached tree blob reuse
- **WHEN** a tree blob was downloaded during a previous `ls` or `restore`
- **THEN** subsequent operations SHALL use the cached version without contacting Azure

#### Scenario: Cache on mounted volume in Docker
- **WHEN** running in Docker
- **THEN** the cache directory (`~/.arius/cache/`) SHALL be on a mounted volume to persist across container restarts

### Requirement: Per-repository cache identification
The local cache SHALL be organized by repository, identified by `SHA256(accountname + container)[:12]`. The cache path SHALL be `~/.arius/cache/<repo-id>/`.

#### Scenario: Two repositories on same machine
- **WHEN** archiving to two different Azure containers
- **THEN** each SHALL have its own cache directory with a distinct repo-id

### Requirement: Rehydration via blob copy
For restore, archive-tier chunks SHALL be rehydrated by copying to `chunks-rehydrated/` in Hot tier (not rehydrated in place). The copy operation SHALL specify the rehydration priority (Standard or High). The system SHALL check for existing rehydrated copies before initiating new rehydration.

#### Scenario: Rehydrate to Hot tier
- **WHEN** chunk `abc123` needs rehydration with Standard priority
- **THEN** the system SHALL issue a copy-blob from `chunks/abc123` to `chunks-rehydrated/abc123` with rehydrate priority Standard

#### Scenario: Already rehydrated
- **WHEN** `chunks-rehydrated/abc123` already exists
- **THEN** the system SHALL skip rehydration and use the existing copy
