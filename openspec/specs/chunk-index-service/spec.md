# Chunk Index Service Spec

## Purpose

Defines `ChunkIndexService`: the shared repository index that maps content hashes to chunk metadata, owns mutable shard lookup and persistence, and keeps chunk metadata concerns separate from chunk blob storage mechanics.

## Requirements

### Requirement: ChunkIndexService shared metadata index
The system SHALL provide `ChunkIndexService` in `Arius.Core/Shared/ChunkIndex/` as the single shared service responsible for content-hash lookup, pending `ShardEntry` recording, shard flush, and in-memory shard-cache invalidation. Chunk metadata lookup SHALL remain separate from chunk blob upload, download, hydration, and cleanup behavior owned by `ChunkStorageService`.

#### Scenario: Archive handler uses chunk index for dedup metadata
- **WHEN** `ArchiveCommandHandler` archives a file
- **THEN** it SHALL use `ChunkIndexService` for dedup lookup and shard recording
- **AND** it SHALL use `ChunkStorageService` for large, tar, and thin chunk blob operations

#### Scenario: Restore handler resolves chunk metadata through index
- **WHEN** `RestoreCommandHandler` restores files from a snapshot
- **THEN** it SHALL use `ChunkIndexService` to resolve content hashes to chunk metadata
- **AND** it SHALL use `ChunkStorageService` to download chunks, resolve hydration state, start rehydration, and plan rehydrated cleanup

#### Scenario: List query uses index for file sizes
- **WHEN** `ListQueryHandler` streams repository file entries
- **THEN** it SHALL retrieve file size metadata from `ChunkIndexService` rather than from filetree blobs

### Requirement: Chunk index entry model
The chunk index SHALL map each content hash to a chunk hash, original size, and compressed size. For large chunks, the content hash equals the chunk hash. For tar-bundled files, the content hash maps to the tar chunk hash. The in-memory entry model SHALL preserve all four values: content hash, chunk hash, original size, and compressed size.

#### Scenario: Large chunk metadata
- **WHEN** a large file chunk is recorded in the chunk index
- **THEN** the entry SHALL have content hash equal to chunk hash
- **AND** it SHALL include original and compressed sizes

#### Scenario: Tar-bundled file metadata
- **WHEN** a small file is bundled into a tar chunk
- **THEN** the entry SHALL map the file's content hash to the parent tar chunk hash
- **AND** it SHALL include the file's original size and proportional compressed size

### Requirement: Shard serialization format
Chunk index shards SHALL use newline-delimited plaintext entries before any remote compression or encryption. Large chunk entries SHALL be serialized as 3 space-separated fields: `<content-hash> <original-size> <compressed-size>`. Tar-bundled file entries SHALL be serialized as 4 space-separated fields: `<content-hash> <chunk-hash> <original-size> <compressed-size>`. On parsing a 3-field line, the system SHALL reconstruct the in-memory entry by setting chunk hash equal to content hash.

#### Scenario: Large file entry serialized as 3 fields
- **WHEN** a shard entry has content hash equal to chunk hash
- **THEN** the entry SHALL be serialized as `<content-hash> <original-size> <compressed-size>`

#### Scenario: Small file entry serialized as 4 fields
- **WHEN** a shard entry has content hash different from chunk hash
- **THEN** the entry SHALL be serialized as `<content-hash> <chunk-hash> <original-size> <compressed-size>`

#### Scenario: Parsing a 3-field entry
- **WHEN** a shard line contains exactly 3 space-separated fields
- **THEN** the system SHALL parse it as a large chunk entry and set chunk hash equal to content hash in the in-memory model

#### Scenario: Parsing a 4-field entry
- **WHEN** a shard line contains exactly 4 space-separated fields
- **THEN** the system SHALL parse it as a tar-bundled file entry with an explicit chunk hash

### Requirement: Tiered shard cache
The system SHALL implement a three-tier cache for chunk index shards: L1 in-memory LRU, L2 local disk cache, and L3 remote blobs under `chunk-index/`. The L1 cache SHALL have a configurable memory budget via `--dedup-cache-mb`, defaulting to 512 MB. Shards promoted to L1 SHALL evict the least-recently-used shard when the memory budget is exceeded.

#### Scenario: L1 cache hit
- **WHEN** a shard was recently accessed and is in the in-memory LRU
- **THEN** lookup SHALL return without disk or network I/O

#### Scenario: L2 cache hit
- **WHEN** a shard is not in L1 but was previously downloaded to disk
- **THEN** the shard SHALL be loaded from disk, promoted to L1, and returned

#### Scenario: L3 cache miss
- **WHEN** a shard has never been accessed on the current machine
- **THEN** the shard SHALL be downloaded from remote storage, saved to L2, promoted to L1, and returned

#### Scenario: New shard
- **WHEN** a shard does not exist in remote storage
- **THEN** the service SHALL treat it as an empty shard

### Requirement: Local and remote shard formats
The L2 local disk cache SHALL store shards as plaintext lines only, with no gzip compression and no encryption. The L2 format SHALL NOT change based on whether a passphrase is provided. Remote L3 shard blobs SHALL store the same logical plaintext lines after gzip compression and optional encryption, using `application/aes256cbc+gzip` when encrypted or `application/gzip` when not encrypted.

#### Scenario: L2 cache stores plaintext
- **WHEN** a shard is saved to the local L2 disk cache
- **THEN** the file SHALL contain raw plaintext lines with no compression or encryption, regardless of whether a passphrase is configured

#### Scenario: L3 upload uses wire format
- **WHEN** a shard is uploaded to remote storage
- **THEN** the blob SHALL be gzip-compressed and AES-256-CBC encrypted if a passphrase is provided, or gzip-compressed only if no passphrase is provided

#### Scenario: Stale L2 file is self-healing
- **WHEN** an L2 cache file cannot be parsed as plaintext lines
- **THEN** the system SHALL treat it as a cache miss, fall through to L3, and re-cache the shard in plaintext format

### Requirement: Shard merge and flush
The system SHALL collect new chunk index entries during archive and flush updated shards before publishing the snapshot. For each modified shard prefix, `ChunkIndexService` SHALL load the existing shard from cache or remote storage, merge new entries, upload the updated shard, and update local caches. If remote storage has no existing shard for a modified prefix, the service SHALL create a new shard.

#### Scenario: New entries merged into existing shard
- **WHEN** 50 new files have content hashes with the same shard prefix
- **THEN** the service SHALL load that shard, merge the 50 entries, and upload the updated shard

#### Scenario: First archive creates shards
- **WHEN** archiving to an empty repository
- **THEN** the service SHALL create new shards for each prefix that has entries

#### Scenario: Snapshot waits for index flush
- **WHEN** an archive run has pending chunk index entries
- **THEN** the archive pipeline SHALL complete `ChunkIndexService.FlushAsync` before creating the snapshot

### Requirement: Mutable shard cache invalidation
Chunk index shards are mutable repository metadata. When snapshot comparison shows that another machine may have updated the repository, the system SHALL invalidate stale chunk index cache state by deleting all files in `~/.arius/{repo}/chunk-index/` and calling `ChunkIndexService.InvalidateL1()` before future lookups trust cached shard contents.

#### Scenario: Snapshot mismatch invalidates chunk index cache
- **WHEN** a snapshot mismatch is detected during cache validation
- **THEN** all files in `~/.arius/{repo}/chunk-index/` SHALL be deleted
- **AND** `ChunkIndexService.InvalidateL1()` SHALL be called

#### Scenario: Next lookup reloads from remote storage
- **WHEN** the chunk index cache was invalidated after a snapshot mismatch
- **THEN** the next lookup SHALL re-download needed shards from remote storage instead of trusting stale local cache data

### Requirement: Repository-local paths are external to chunk index
The system SHALL provide repository-scoped path helpers for local cache and log directories instead of exposing repository directory naming helpers from `ChunkIndexService`. Shared services, CLI code, and tests SHALL use the repository-scoped helper for repo root, chunk-index cache, filetree cache, snapshot cache, and logs directories.

#### Scenario: Shared services use repository path helper
- **WHEN** shared services derive local cache directories
- **THEN** they SHALL do so through the shared repository path helper rather than through static methods on `ChunkIndexService`

#### Scenario: CLI logs path does not depend on chunk index service
- **WHEN** CLI code derives the repository logs directory
- **THEN** it SHALL use the shared repository path helper and SHALL NOT depend on `ChunkIndexService` for the repository directory name
