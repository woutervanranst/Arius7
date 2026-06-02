# Chunk Index Service Spec

## Purpose

Defines `ChunkIndexService`: the shared repository index that maps content hashes to chunk metadata, owns mutable shard lookup and persistence, and keeps chunk metadata concerns separate from chunk blob storage mechanics.
## Requirements
### Requirement: ChunkIndexService shared metadata index
The system SHALL provide public `ChunkIndexService` in `Arius.Core/Shared/ChunkIndex/` as the single shared facade responsible for content-hash lookup, pending `ShardEntry` recording, shard flush, and in-memory shard-cache invalidation. Chunk metadata lookup SHALL remain separate from chunk blob upload, download, hydration, and cleanup behavior owned by `ChunkStorageService`.

The `ChunkIndexService` implementation SHALL keep read-through shard cache mechanics, read-only lookup behavior, and archive write-session buffering as separate internal responsibilities. The facade SHALL preserve current behavior for callers while delegating those responsibilities to focused internal components.

`ChunkIndexService` SHALL remain the operational boundary for chunk-index behavior and SHALL remain public during this change. Extracted chunk-index components SHALL be internal implementation details and SHALL NOT be registered as separate DI services or consumed directly by feature handlers, other shared services, CLI code, storage code, or user-facing tests. `ChunkIndexService` SHALL construct those internal collaborators from its own dependencies rather than resolving them from the service collection. Architecture tests SHALL enforce that callers outside the chunk-index implementation use `ChunkIndexService` rather than the extracted reader, write-session, or shard cache/store components.

`Shard` SHALL be treated as an owned mutable in-memory shard page. The implementation SHALL remove copy-on-merge shard mutation and provide explicit mutation operations such as add-or-update entry/range behavior. `Shard` SHALL NOT use an internal concurrent dictionary solely for this change. The extracted shard cache/store SHALL own async per-prefix synchronization for operations that read, load, mutate, save, upload, or promote a shard. Read-only lookup and update/rebuild operations for the same prefix SHALL use the same prefix gate so readers cannot observe a mutable cached shard while it is being mutated, saved, uploaded, or promoted. The shard cache/store SHALL NOT expose cached mutable `Shard` instances as long-lived caller-owned objects outside prefix-scoped read or update operations. Persisted shard serialization SHALL remain deterministic by writing entries sorted by content hash.

The extracted write session SHALL make concurrent `AddEntry` calls safe for archive workers without serializing normal entry recording through a write-session lock. Session-overlay and pending-flush state MAY be represented by the same content-hash-keyed concurrent collection when duplicate entries use last-writer-wins semantics. `FlushAsync` SHALL be treated as an archive-tail operation that starts after archive workers have stopped recording entries. It SHALL prevent concurrent flushes, snapshot the current session entries before shard-cache/store I/O, and clear session state only after all touched prefixes have flushed successfully. `AddEntry` SHALL fail fast when it observes a flush already in progress, but the write session does not need to provide an atomic check-and-add boundary for calls racing a flush start because that race is outside the archive pipeline contract.

`ChunkIndexService` SHALL own repair orchestration and repair-in-progress marker enforcement. Normal operations that trust or mutate chunk-index state, including lookup, entry recording, and pending-entry flush, SHALL check the repair marker at the facade boundary before delegating to extracted components. Extracted components SHALL NOT independently reject shard-cache/store operations solely because the repair marker exists, so explicit repair can rebuild local shard state and upload repaired shards while the marker is present.

Automated test coverage for `src/Arius.Core/Shared/ChunkIndex/`, including the extracted internal components, SHALL remain above 90% after this change. Existing chunk-index tests SHALL be split or renamed so each extracted responsibility has focused coverage while facade behavior remains covered through `ChunkIndexService` tests.

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

#### Scenario: Internal responsibilities are separated
- **WHEN** chunk-index lookup, archive entry recording, shard flush, or cache invalidation is executed through `ChunkIndexService`
- **THEN** the facade SHALL delegate read-through shard cache mechanics, read-only lookup behavior, and archive write-session state to separate internal components
- **AND** feature handlers SHALL NOT need to compute shard prefixes or access those internal components directly

#### Scenario: Shards are mutable pages behind the shard cache/store boundary
- **WHEN** a shard is loaded for flush or repair update
- **THEN** the implementation SHALL update the owned in-memory shard page through explicit add-or-update mutation
- **AND** it SHALL NOT create a replacement shard through `Shard.Merge`
- **AND** persisted shard output SHALL remain sorted by content hash

#### Scenario: Shard cache/store synchronizes mutable shard operations by prefix
- **WHEN** chunk-index code reads, loads, mutates, saves, uploads, or promotes a shard for a prefix
- **THEN** the shard cache/store SHALL synchronize that operation at the shard-prefix boundary
- **AND** no two mutation/save/upload sequences for the same prefix SHALL run concurrently through the shard cache/store
- **AND** `Shard` itself SHALL NOT use a concurrent dictionary solely to provide this synchronization
- **AND** the implementation SHALL NOT hold the L1 LRU lock across asynchronous I/O

#### Scenario: Read-only lookup does not observe in-progress shard mutation
- **WHEN** a lookup and a flush or repair update target the same shard prefix
- **THEN** both operations SHALL use the same prefix-scoped shard cache/store gate
- **AND** the lookup SHALL return copied lookup results rather than a cached mutable `Shard` instance
- **AND** it SHALL NOT observe the shard page while the update is mutating, saving, uploading, or promoting that prefix

#### Scenario: Mutable cached shards are not handed out as caller-owned objects
- **WHEN** the read-only reader or write session needs shard contents for a prefix
- **THEN** it SHALL access those contents through prefix-scoped shard cache/store operations
- **AND** the shard cache/store SHALL NOT return a cached mutable `Shard` instance for callers to hold and mutate outside the cache/store synchronization boundary

#### Scenario: Concurrent entry recording is safe
- **WHEN** multiple archive workers record chunk-index entries through `ChunkIndexService.AddEntry`
- **THEN** the write session SHALL record each entry in same-session overlay and pending flush state without corrupting concurrent writer state
- **AND** same-service lookups SHALL be able to resolve recorded session entries without corrupting write-session state
- **AND** duplicate content hashes SHALL use last-writer-wins semantics

#### Scenario: Flush snapshots stable archive-tail state
- **WHEN** `FlushAsync` starts processing pending write-session entries
- **THEN** it SHALL reject concurrent flush calls
- **AND** it SHALL snapshot the current session entries before performing shard-cache/store I/O
- **AND** it SHALL fail fast when `AddEntry` observes that the flush is already in progress
- **AND** archive command orchestration SHALL await entry-producing workers before starting the flush
- **AND** it SHALL clear session and pending state only after all touched prefixes have flushed successfully

#### Scenario: Batched lookup applies the write-session overlay before persisted lookup
- **WHEN** `ChunkIndexService` performs batched lookup for content hashes that include entries recorded in the current archive session
- **THEN** the facade SHALL resolve those session entries before delegating to the read-only reader
- **AND** the read-only reader SHALL receive only content hashes that missed the session overlay
- **AND** the facade SHALL merge session hits with persisted-index results before returning to the caller

#### Scenario: Batched persisted lookup groups misses by shard prefix
- **WHEN** the read-only reader receives multiple content hashes for persisted-index lookup
- **THEN** it SHALL group those hashes by fixed shard prefix
- **AND** it SHALL use the shard cache/store at most once per prefix for that batched lookup
- **AND** it SHALL return hits while omitting misses from the result

#### Scenario: Write-session state clears only after whole flush succeeds
- **WHEN** pending write-session entries touch multiple shard prefixes
- **AND** `FlushAsync` starts writing those shard prefixes
- **THEN** the write session SHALL keep session and pending entries until all touched prefixes have been flushed successfully
- **AND** if any prefix flush fails, `FlushAsync` SHALL fail without clearing the write-session state as successfully flushed

#### Scenario: Extracted components use the existing fixed-layout constants
- **WHEN** extracted chunk-index components need shard-prefix or flush-worker policy during this change
- **THEN** they SHALL use the existing internal constants on `ChunkIndexService`
- **AND** the change SHALL NOT introduce a separate chunk-index layout/options abstraction solely for the current fixed two-character prefix layout
- **AND** code outside `Shared/ChunkIndex` SHALL NOT compute or persist chunk-index prefix lengths or shard-routing state

#### Scenario: Facade boundary is enforced by architecture tests
- **WHEN** architecture tests inspect dependencies on extracted chunk-index reader, write-session, and shard cache/store components
- **THEN** code outside the chunk-index implementation SHALL NOT depend on those components directly
- **AND** feature handlers, DI registration, CLI code, storage code, restore/list/archive workflows, and other shared services SHALL use `ChunkIndexService` as the chunk-index operation boundary

#### Scenario: Extracted components are constructed inside the facade
- **WHEN** `ChunkIndexService` initializes its extracted reader, write-session, and shard cache/store collaborators
- **THEN** it SHALL construct them as internal implementation objects from its own dependencies
- **AND** `ServiceCollectionExtensions` SHALL NOT register those extracted components separately

#### Scenario: Repair marker is enforced at the facade boundary
- **WHEN** the repair in-progress marker exists
- **THEN** normal lookup, entry recording, and flush calls through `ChunkIndexService` SHALL fail before delegating to extracted components
- **AND** explicit repair through `ChunkIndexService` SHALL still be able to use internal shard-cache/store operations while the marker exists

#### Scenario: New components have focused coverage
- **WHEN** tests are run with coverage for `src/Arius.Core/Shared/ChunkIndex/`
- **THEN** line coverage SHALL be greater than 90%
- **AND** the coverage SHALL include focused tests for the extracted shard cache/store, read-only reader, and write-session responsibilities

#### Scenario: Existing tests are split by responsibility
- **WHEN** the current `ChunkIndexService` tests are updated for the split
- **THEN** facade behavior SHALL remain covered through `ChunkIndexService` tests
- **AND** implementation-detail behavior that moved into extracted components SHALL be covered by focused component tests rather than one broad service test file

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

### Requirement: Fixed shard prefix layout
The chunk index SHALL derive shard prefixes from one internal repository-wide prefix-length constant on `ChunkIndexService`. All chunk-index lookup, flush, repair, local cache paths, and remote shard paths SHALL use that same prefix length. The prefix length SHALL NOT be configurable through the CLI. `ContentHash` SHALL expose a general `Prefix(int length)` method for prefix extraction, and prefix calculation SHALL NOT add prefix-length-specific properties such as `Prefix2` or `Prefix3`.

This fixed prefix-length layout SHALL be treated as an interim internal routing decision. Feature callers SHALL NOT accept, compute, or persist chunk-index prefix lengths; they SHALL use chunk-index service APIs so a future dynamic-sharding layout can replace prefix routing behind that boundary.

Future chunk-index shard-routing or layout metadata SHALL remain internal to `ChunkIndexService`. Feature handlers, filetree code, snapshot code, repair callers, and storage callers SHALL NOT persist, compute, expose, or branch on chunk-index prefix lengths or shard-routing state.

#### Scenario: Prefix calculation uses layout constant
- **WHEN** the chunk index calculates the shard prefix for a content hash
- **THEN** it SHALL use `ContentHash.Prefix(ChunkIndexService.ShardPrefixLength)`

#### Scenario: ContentHash exposes general prefix extraction
- **WHEN** code needs a variable-length content hash prefix
- **THEN** it SHALL use `ContentHash.Prefix(int length)`
- **AND** it SHALL NOT add a new prefix-length-specific property for that length

#### Scenario: Shard path uses calculated prefix
- **WHEN** a shard prefix is calculated as `aa`
- **THEN** the remote shard blob SHALL be addressed as `chunk-index/aa`
- **AND** the local L2 shard file SHALL be addressed under the chunk-index cache using the same prefix

### Requirement: Chunk index test coverage
Automated tests SHALL cover at least 90% of code under `src/Arius.Core/Shared/ChunkIndex/`.

#### Scenario: Shared chunk index coverage threshold
- **WHEN** coverage is measured for `src/Arius.Core/Shared/ChunkIndex/`
- **THEN** line coverage SHALL be at least 90%

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

### Requirement: Lookup failure behavior
The chunk index lookup API SHALL NOT repair chunk-index state during normal archive, restore, or list operations. If a requested shard blob is missing from remote storage, lookup SHALL treat that shard as empty and return misses for hashes in that prefix. If a local L2 shard file is corrupt, lookup MAY delete that local cache file and reload the shard from remote storage. If a remote shard blob exists but cannot be deserialized, lookup SHALL fail with a clear chunk-index corruption error that instructs the user to run the explicit chunk-index repair command. If local repair state indicates that an explicit full repair was interrupted before completing, lookup SHALL fail with a clear repair-incomplete error that instructs the user to rerun the explicit chunk-index repair command.

Remote shard deserialization failures SHALL be reported with a dedicated `ChunkIndexCorruptException`. Repair in-progress marker failures SHALL be reported with a dedicated `ChunkIndexRepairIncompleteException`. These exceptions SHALL include a user-facing message that identifies the chunk-index condition and instructs the user to run the explicit chunk-index repair command.

Valid shards SHALL be trusted. If a shard exists and parses but does not contain a requested content hash, lookup SHALL return a miss and SHALL NOT automatically repair that prefix or run full repair.

#### Scenario: Missing shard returns miss
- **WHEN** lookup is called and the requested shard is missing from remote storage
- **THEN** lookup SHALL treat the shard as empty and return no entry for hashes in that prefix
- **AND** it SHALL NOT list `chunks/` or rewrite the chunk-index shard

#### Scenario: Corrupt remote shard fails clearly
- **WHEN** lookup loads a remote shard blob that cannot be deserialized
- **THEN** lookup SHALL fail with a clear chunk-index corruption error
- **AND** the error SHALL instruct the user to run the explicit chunk-index repair command
- **AND** lookup SHALL NOT rebuild or rewrite the shard automatically

#### Scenario: Interrupted repair fails clearly
- **WHEN** lookup starts and local chunk-index repair state indicates an interrupted full repair
- **THEN** lookup SHALL fail with a clear repair-incomplete error
- **AND** the error SHALL instruct the user to rerun the explicit chunk-index repair command

#### Scenario: Valid shard missing entry is trusted
- **WHEN** lookup loads a valid shard for prefix `aa`
- **AND** the requested content hash is not present in that shard
- **THEN** lookup SHALL return a miss for that content hash
- **AND** it SHALL NOT rebuild prefix `aa` automatically

### Requirement: Repair reconstructs index entries from chunks
Full repair SHALL rebuild chunk-index shards from committed chunk blobs. Large chunk blobs SHALL reconstruct entries where content hash equals chunk hash. Thin chunk blobs SHALL reconstruct entries where content hash maps to the parent tar chunk hash stored in thin chunk metadata key `parent_chunk_hash`. Tar chunk blobs SHALL NOT directly create chunk-index entries because thin chunks are the per-file mapping source. Chunk blobs without recognized `arius_type` metadata SHALL be ignored because `arius_type` is the completion sentinel.

Thin chunks SHALL include `parent_chunk_hash`, `original_size`, and `compressed_size` metadata. A thin chunk with `arius_type: thin` but missing or invalid required metadata SHALL cause full repair to fail with a clear chunk-index repair error instead of silently omitting the mapping.

#### Scenario: Large chunk reconstructed
- **WHEN** full repair sees `chunks/aa123...` with metadata `arius_type: large`
- **THEN** it SHALL add a shard entry with content hash `aa123...` and chunk hash `aa123...`
- **AND** it SHALL use the blob metadata for original and compressed sizes

#### Scenario: Thin chunk reconstructed
- **WHEN** full repair sees `chunks/aa456...` with metadata `arius_type: thin`
- **AND** metadata includes valid `parent_chunk_hash`, `original_size`, and `compressed_size` values
- **THEN** it SHALL add a shard entry mapping content hash `aa456...` to the parent tar chunk hash from metadata
- **AND** it SHALL use the thin chunk metadata for original and compressed sizes
- **AND** it SHALL NOT download the thin chunk body to reconstruct the entry

#### Scenario: Committed thin chunk with invalid metadata fails repair
- **WHEN** full repair sees `chunks/aa456...` with metadata `arius_type: thin`
- **AND** required thin chunk metadata is missing or invalid
- **THEN** full repair SHALL fail with a clear chunk-index repair error
- **AND** it SHALL NOT silently omit the thin chunk mapping from rebuilt shards

#### Scenario: Tar chunk ignored directly
- **WHEN** full repair sees a chunk blob with metadata `arius_type: tar`
- **THEN** it SHALL NOT add a shard entry for the tar blob itself

#### Scenario: Partial or unknown chunk ignored
- **WHEN** full repair sees a chunk blob without `arius_type` metadata or with an unrecognized `arius_type` value
- **THEN** it SHALL NOT add a shard entry for that blob

### Requirement: Explicit full chunk-index repair
The system SHALL provide an explicit full chunk-index repair API and command that rebuilds all chunk-index shards from chunk blobs using the configured shard prefix length. Full repair SHALL purge the local L2 chunk-index cache, scan committed chunk blobs once with `ListAsync("chunks/", includeMetadata: true, ...)`, reconstruct large and thin entries, group reconstructed entries by shard prefix in memory, write rebuilt local L2 shard files for each non-empty prefix, upload every rebuilt non-empty shard to `chunk-index/<prefix>`, and retain the rebuilt L2 files as the current local chunk-index cache. This in-memory prefix grouping SHALL be treated as the current repair behavior preserved by this responsibility split; full repair SHALL NOT become a streaming disk-backed rebuild workflow in this change. Full repair SHALL NOT write empty shard blobs. Committed chunk blobs are append-only repository data; full repair SHALL treat chunk storage as the durable source for chunk-index reconstruction.

Full repair assumes no concurrent archive or repair operation is mutating the same remote archive. Distributed locking or remote repair leases are out of scope.

Full repair SHALL invalidate chunk-index L1 cache state and mark the local L2 rebuild as in progress before purging or writing local shard-cache files. Normal chunk-index lookups SHALL fail clearly if a previous full repair was interrupted before completing. Full repair SHALL be allowed to run when this marker already exists, and it SHALL purge the partial local rebuild before reconstructing shard contents again. Full repair SHALL clear the in-progress marker only after rebuilt shards have been uploaded and stale remote shards have been deleted.

The repair in-progress marker SHALL be addressed through an internal `ChunkIndexService` constant and SHALL live outside the purgeable local shard-cache directory, for example `~/.arius/{repo}/chunk-index.repair-in-progress`. Chunk-index cache invalidation SHALL NOT delete the repair in-progress marker.

Normal chunk-index operations that trust or mutate chunk-index state, including lookup, entry recording, and pending-entry flush, SHALL check for the repair in-progress marker at operation start and throw `ChunkIndexRepairIncompleteException` when the marker exists. The `ChunkIndexService` constructor SHALL NOT fail solely because the repair in-progress marker exists, so the explicit repair command can construct the service and rerun repair. The explicit full repair API SHALL be allowed to run when the repair in-progress marker already exists. Cache invalidation that only clears local cache state MAY run while the marker exists, but SHALL NOT delete the marker.

This requirement intentionally supersedes the previous disk-backed rebuild wording from `improve-chunk-index-scalability` for this responsibility split. Full repair SHALL use the current implemented shape: in-memory prefix grouping followed by complete rebuilt-shard replacement. Bounded disk-backed repair MAY be introduced by a later hardening change, but it is not the required behavior for this change.

Full repair SHALL process rebuilt shard prefixes with bounded `Parallel.ForEachAsync` after the one metadata-aware chunk listing has completed. Each rebuilt prefix SHALL be handled by at most one worker, and each worker SHALL write the rebuilt local L2 shard file and upload the corresponding remote shard for its prefix. Full repair SHALL remember the set of shard prefixes that produced entries during the repair run. After rebuilt shards have been uploaded, full repair SHALL list existing blobs under `chunk-index/` and delete shard blobs whose names are not in that rebuilt prefix set.

Full repair SHALL be idempotent and safe to rerun. If full repair is interrupted while rebuilding local L2 shard files, while uploading rebuilt shards, or after uploading rebuilt shards but before deleting stale remote shard blobs, a later full repair SHALL purge the partial local rebuild and reconstruct shard contents from committed chunks again. Full repair SHALL NOT publish snapshots.

#### Scenario: Full repair rebuilds all touched prefixes
- **WHEN** full chunk-index repair runs on a repository with large and thin chunk blobs
- **THEN** it SHALL reconstruct shard entries for all large and thin chunks
- **AND** it SHALL group reconstructed entries by shard prefix in memory
- **AND** it SHALL write local L2 shard files for every prefix that has reconstructed entries
- **AND** it SHALL upload chunk-index shards for every prefix that has reconstructed entries
- **AND** it SHALL NOT write empty chunk-index shards

#### Scenario: Full repair does not stream entries into L2 during this split
- **WHEN** full chunk-index repair scans committed chunk blobs
- **THEN** it SHALL group reconstructed entries by shard prefix in memory before writing rebuilt local L2 shard files
- **AND** it SHALL NOT merge each reconstructed entry directly into local L2 shard state as part of this responsibility split

#### Scenario: Full repair scans chunks once
- **WHEN** full chunk-index repair runs
- **THEN** it SHALL perform one metadata-aware listing for `chunks/`
- **AND** it SHALL NOT rebuild by issuing one chunk listing per possible shard prefix

#### Scenario: Full repair writes rebuilt prefixes in parallel
- **WHEN** full chunk-index repair has grouped reconstructed entries by shard prefix
- **THEN** it SHALL process rebuilt shard prefixes with bounded `Parallel.ForEachAsync`
- **AND** each rebuilt prefix SHALL be processed by at most one worker
- **AND** each worker SHALL write the rebuilt local L2 shard and upload the rebuilt remote shard for its prefix

#### Scenario: Full repair deletes stale shards
- **WHEN** full chunk-index repair completes local shard reconstruction and uploads rebuilt shards
- **AND** an existing `chunk-index/` shard blob is not in the expected rebuilt prefix set
- **THEN** full repair SHALL delete that stale shard blob

#### Scenario: Full repair rebuilds local cache safely
- **WHEN** full chunk-index repair starts
- **THEN** it SHALL invalidate chunk-index L1 cache state
- **AND** it SHALL mark the L2 rebuild as in progress before purging existing L2 chunk-index cache files
- **AND** it SHALL purge existing L2 chunk-index cache files before writing rebuilt local shard files
- **AND** it SHALL keep the in-progress marker until remote upload and stale-shard deletion complete

#### Scenario: Repair marker survives cache invalidation
- **WHEN** chunk-index cache invalidation deletes local shard-cache files
- **THEN** it SHALL NOT delete the repair in-progress marker
- **AND** later normal chunk-index lookups SHALL still fail with a repair-incomplete error while the marker exists

#### Scenario: Interrupted local rebuild is not trusted
- **WHEN** full chunk-index repair was interrupted before clearing its in-progress marker
- **THEN** later chunk-index lookups SHALL fail with a clear repair-incomplete error
- **AND** the error SHALL instruct the user to rerun the explicit chunk-index repair command

#### Scenario: Constructor allows repair command after interruption
- **WHEN** `ChunkIndexService` is constructed and the repair in-progress marker exists
- **THEN** construction SHALL succeed
- **AND** the explicit full repair API SHALL be callable so repair can be rerun

#### Scenario: Normal operations check repair marker
- **WHEN** the repair in-progress marker exists
- **THEN** normal chunk-index lookup, entry recording, and flush operations SHALL fail with a clear repair-incomplete error
- **AND** the error SHALL instruct the user to rerun the explicit chunk-index repair command

#### Scenario: Repair rerun after interrupted local rebuild
- **WHEN** full chunk-index repair starts and the in-progress marker already exists
- **THEN** full repair SHALL purge the partial local rebuild
- **AND** it SHALL reconstruct shard contents again from committed chunks

#### Scenario: Full repair available for maintenance
- **WHEN** an operator invokes the full chunk-index repair command
- **THEN** the command SHALL rebuild the remote chunk index from chunk blobs without requiring archive reruns

#### Scenario: Committed chunks are repair source of truth
- **WHEN** full chunk-index repair runs
- **THEN** it SHALL derive chunk-index entries from committed large and thin chunk blobs
- **AND** it SHALL NOT require existing chunk-index shard contents to reconstruct those entries

#### Scenario: Full repair can be rerun after interruption
- **WHEN** full chunk-index repair is interrupted after writing only some local or remote shard prefixes
- **THEN** rerunning full repair SHALL reconstruct shard contents from committed chunks again
- **AND** it SHALL be able to complete without relying on the partial repair output

#### Scenario: Full repair does not publish snapshot
- **WHEN** full chunk-index repair writes repaired shard blobs
- **THEN** it SHALL NOT create or update any snapshot manifest

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
The system SHALL collect new chunk index entries during archive and flush updated shards before publishing the snapshot. For each modified shard prefix, the chunk-index write session SHALL ask the shard cache/store to load or create the current shard, apply all pending entries for that prefix to the owned mutable shard page with last-writer-wins semantics by content hash, upload the updated shard, update local L2 cache state, and promote the updated shard to L1. If remote storage has no existing shard for a modified prefix, the service SHALL create a new shard. Flush SHALL process independent shard prefixes with bounded parallelism while ensuring a given prefix is handled by only one flush worker. The implementation SHALL NOT use a `Shard.Merge` method for this behavior.

#### Scenario: New entries applied to existing shard
- **WHEN** 50 new files have content hashes with the same shard prefix
- **THEN** the write session SHALL load that shard through the shard cache/store
- **AND** it SHALL apply the 50 entries to the owned mutable shard page
- **AND** it SHALL upload the updated shard and update local caches

#### Scenario: Duplicate pending content hash uses last writer
- **WHEN** pending entries contain multiple entries for the same content hash
- **THEN** the flushed shard SHALL contain the last recorded entry for that content hash

#### Scenario: First archive creates shards
- **WHEN** archiving to an empty repository
- **THEN** the service SHALL create new shards for each prefix that has entries

#### Scenario: Snapshot waits for index flush
- **WHEN** an archive run has pending chunk index entries
- **THEN** the archive pipeline SHALL complete `ChunkIndexService.FlushAsync` before creating the snapshot

#### Scenario: Independent prefixes flush in parallel
- **WHEN** pending chunk index entries touch multiple shard prefixes
- **THEN** the service SHALL process those prefixes using bounded parallelism
- **AND** no two workers SHALL write the same shard prefix concurrently
- **AND** the shard cache/store SHALL also serialize mutation/save/upload operations for the same prefix

#### Scenario: Flush uses internal worker constant
- **WHEN** `ChunkIndexService.FlushAsync` processes touched shard prefixes
- **THEN** it SHALL bound concurrency using an internal worker-count constant on `ChunkIndexService`

#### Scenario: Partial flush failure does not publish snapshot
- **WHEN** parallel chunk-index flush fails after writing some shard prefixes
- **THEN** the archive operation SHALL fail
- **AND** no snapshot SHALL be published for that failed archive run
- **AND** the write session SHALL keep session and pending entries because the whole flush did not succeed

### Requirement: Mutable shard cache invalidation
Chunk index shards are mutable repository metadata. When `FileTreeService.ValidateAsync` returns `FileTreeValidationResult` with `SnapshotMismatch` set, the archive workflow SHALL invalidate stale chunk index cache state by deleting all files in `~/.arius/{repo}/chunk-index/` and calling `ChunkIndexService.InvalidateL1()` before future lookups trust cached shard contents. Chunk-index invalidation SHALL be owned by `ChunkIndexService` and coordinated by the archive workflow, not by `FileTreeService`.

#### Scenario: Filetree validation mismatch invalidates chunk index cache
- **WHEN** `FileTreeService.ValidateAsync` returns a result with `SnapshotMismatch` set
- **THEN** all files in `~/.arius/{repo}/chunk-index/` SHALL be deleted
- **AND** `ChunkIndexService.InvalidateL1()` SHALL be called

#### Scenario: Next lookup reloads from remote storage
- **WHEN** the chunk index cache was invalidated after a snapshot mismatch
- **THEN** the next lookup SHALL re-download needed shards from remote storage instead of trusting stale local cache data

#### Scenario: FileTreeService does not invalidate chunk index
- **WHEN** `FileTreeService` validates or materializes filetree cache state
- **THEN** it SHALL NOT delete chunk-index cache files
- **AND** it SHALL NOT call `ChunkIndexService.InvalidateL1()`

### Requirement: Repository-local paths are external to chunk index
The system SHALL provide repository-scoped path helpers for local cache and log directories instead of exposing repository directory naming helpers from `ChunkIndexService`. Shared services, CLI code, and tests SHALL use the repository-scoped helper for repo root, chunk-index cache, filetree cache, snapshot cache, and logs directories.

#### Scenario: Shared services use repository path helper
- **WHEN** shared services derive local cache directories
- **THEN** they SHALL do so through the shared repository path helper rather than through static methods on `ChunkIndexService`

#### Scenario: CLI logs path does not depend on chunk index service
- **WHEN** CLI code derives the repository logs directory
- **THEN** it SHALL use the shared repository path helper and SHALL NOT depend on `ChunkIndexService` for the repository directory name
