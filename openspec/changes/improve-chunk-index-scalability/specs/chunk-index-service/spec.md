## ADDED Requirements

### Requirement: Fixed shard prefix layout
The chunk index SHALL derive shard prefixes from one internal repository-wide prefix-length constant on `ChunkIndexService`. All chunk-index lookup, flush, repair, local cache paths, and remote shard paths SHALL use that same prefix length. The prefix length SHALL NOT be configurable through the CLI in this change. `ContentHash` SHALL expose a general `Prefix(int length)` method for prefix extraction, and prefix calculation SHALL NOT add prefix-length-specific properties such as `Prefix2` or `Prefix3`.

This fixed prefix-length layout SHALL be treated as an interim internal routing decision. Feature callers SHALL NOT accept, compute, or persist chunk-index prefix lengths; they SHALL use chunk-index service APIs so a future dynamic-sharding layout can replace prefix routing behind that boundary.

#### Scenario: Prefix calculation uses layout constant
- **WHEN** the chunk index calculates the shard prefix for a content hash
- **THEN** it SHALL use `ContentHash.Prefix(ChunkIndexService.ShardPrefixLength)`

#### Scenario: ContentHash exposes general prefix extraction
- **WHEN** code needs a variable-length content hash prefix
- **THEN** it SHALL use `ContentHash.Prefix(int length)`
- **AND** it SHALL NOT add a new prefix-length-specific property for that length

### Requirement: Chunk index test coverage
Automated tests SHALL cover at least 90% of code under `src/Arius.Core/Shared/ChunkIndex/` after this change.

#### Scenario: Shared chunk index coverage threshold
- **WHEN** coverage is measured for `src/Arius.Core/Shared/ChunkIndex/`
- **THEN** line coverage SHALL be at least 90%

#### Scenario: Shard path uses calculated prefix
- **WHEN** a shard prefix is calculated as `aa`
- **THEN** the remote shard blob SHALL be addressed as `chunk-index/aa`
- **AND** the local L2 shard file SHALL be addressed under the chunk-index cache using the same prefix

### Requirement: Lookup repair modes
The chunk index lookup API SHALL support configurable repair behavior. `LookupRepairMode.None` SHALL perform lookup without repair. `LookupRepairMode.OnCorruptShard` SHALL rebuild a prefix from chunk blobs when the shard blob exists but cannot be deserialized. `LookupRepairMode.OnMissingShardProbe` SHALL include corrupt-shard repair and SHALL probe `chunks/<prefix>*` when a requested shard blob is missing; if at least one chunk exists for that prefix, the service SHALL rebuild the prefix and retry lookup, otherwise it SHALL treat the prefix as empty.

Valid shards SHALL be trusted. If a shard exists and parses but does not contain a requested content hash, lookup SHALL return a miss and SHALL NOT automatically repair that prefix.

#### Scenario: No repair mode returns miss
- **WHEN** lookup is called with `LookupRepairMode.None` and the requested shard is missing
- **THEN** lookup SHALL treat the shard as empty and return no entry for hashes in that prefix
- **AND** it SHALL NOT list `chunks/` or rewrite the chunk-index shard

#### Scenario: Corrupt shard is repaired
- **WHEN** lookup is called with `LookupRepairMode.OnCorruptShard` and the shard blob exists but cannot be deserialized
- **THEN** the service SHALL rebuild that shard prefix from `chunks/<prefix>*`
- **AND** it SHALL rewrite `chunk-index/<prefix>`
- **AND** it SHALL retry the lookup using the rebuilt shard

#### Scenario: Missing shard probe finds chunks
- **WHEN** lookup is called with `LookupRepairMode.OnMissingShardProbe` and `chunk-index/aa` is missing
- **AND** probing `chunks/aa*` finds at least one chunk blob
- **THEN** the service SHALL rebuild prefix `aa` from `chunks/aa*`
- **AND** it SHALL retry the lookup using the rebuilt shard

#### Scenario: Missing shard probe finds no chunks
- **WHEN** lookup is called with `LookupRepairMode.OnMissingShardProbe` and `chunk-index/aa` is missing
- **AND** probing `chunks/aa*` finds no chunk blob
- **THEN** the service SHALL treat prefix `aa` as empty
- **AND** it SHALL NOT write a new chunk-index shard for that empty prefix

#### Scenario: Valid shard missing entry is trusted
- **WHEN** lookup loads a valid shard for prefix `aa`
- **AND** the requested content hash is not present in that shard
- **THEN** lookup SHALL return a miss for that content hash
- **AND** it SHALL NOT rebuild prefix `aa` automatically

### Requirement: Prefix repair reconstructs index entries from chunks
Prefix repair SHALL rebuild a chunk-index shard by listing chunk blobs whose names start with `chunks/<prefix>`. Large chunk blobs SHALL reconstruct entries where content hash equals chunk hash. Thin chunk blobs SHALL reconstruct entries where content hash maps to the tar chunk hash stored in the thin chunk body. Tar chunk blobs SHALL NOT directly create chunk-index entries because thin chunks are the per-file mapping source. Chunk blobs without recognized `arius-type` metadata SHALL be ignored because `arius-type` is the completion sentinel.

#### Scenario: Large chunk reconstructed
- **WHEN** prefix repair sees `chunks/aa123...` with metadata `arius-type: large`
- **THEN** it SHALL add a shard entry with content hash `aa123...` and chunk hash `aa123...`
- **AND** it SHALL use the blob metadata for original and compressed sizes

#### Scenario: Thin chunk reconstructed
- **WHEN** prefix repair sees `chunks/aa456...` with metadata `arius-type: thin`
- **THEN** it SHALL read the thin chunk body to obtain the parent tar chunk hash
- **AND** it SHALL add a shard entry mapping content hash `aa456...` to that parent tar chunk hash
- **AND** it SHALL use the thin chunk metadata for original and compressed sizes

#### Scenario: Tar chunk ignored directly
- **WHEN** prefix repair sees a chunk blob with metadata `arius-type: tar`
- **THEN** it SHALL NOT add a shard entry for the tar blob itself

#### Scenario: Partial or unknown chunk ignored
- **WHEN** prefix repair sees a chunk blob without `arius-type` metadata or with an unrecognized `arius-type` value
- **THEN** it SHALL NOT add a shard entry for that blob

### Requirement: Explicit full chunk-index repair
The system SHALL provide an explicit full chunk-index repair API and command that rebuilds all chunk-index shards from chunk blobs using the configured shard prefix length. Full repair SHALL enumerate the valid shard prefixes for the current layout, rebuild each prefix by listing committed chunk blobs under `chunks/<prefix>*`, reconstruct large and thin entries for that prefix, and overwrite the complete live shard blob under `chunk-index/<prefix>` in place when the rebuilt prefix contains entries. Full repair SHALL NOT write empty shard blobs. Committed chunk blobs are append-only repository data; full repair SHALL treat chunk storage as the durable source for chunk-index reconstruction.

Full repair SHALL remember the set of shard prefixes that produced entries during the repair run. After all prefixes have been processed, full repair SHALL list existing blobs under `chunk-index/` and delete shard blobs whose names are not in that expected prefix set. Full repair SHALL invalidate chunk-index L1 and L2 cache state after repair completes.

Full repair SHALL be idempotent and safe to rerun. If full repair is interrupted after writing some shard prefixes but before writing others, or after writing prefixes but before deleting stale shard blobs, a later full repair SHALL converge by reconstructing all shard contents again from committed chunk blobs. Full repair SHALL NOT publish snapshots.

#### Scenario: Full repair rebuilds all touched prefixes
- **WHEN** full chunk-index repair runs on a repository with large and thin chunk blobs
- **THEN** it SHALL reconstruct shard entries for all large and thin chunks
- **AND** it SHALL overwrite chunk-index shards for every prefix that has reconstructed entries
- **AND** it SHALL NOT write empty chunk-index shards

#### Scenario: Full repair deletes stale shards
- **WHEN** full chunk-index repair completes prefix reconstruction
- **AND** an existing `chunk-index/` shard blob is not in the expected rebuilt prefix set
- **THEN** full repair SHALL delete that stale shard blob

#### Scenario: Full repair invalidates local caches
- **WHEN** full chunk-index repair completes
- **THEN** it SHALL invalidate chunk-index L1 cache state
- **AND** it SHALL delete stale L2 chunk-index cache files before future lookups trust cached shard contents

#### Scenario: Full repair available for maintenance
- **WHEN** an operator invokes the full chunk-index repair command
- **THEN** the command SHALL rebuild the remote chunk index from chunk blobs without requiring archive reruns

#### Scenario: Committed chunks are repair source of truth
- **WHEN** full chunk-index repair runs
- **THEN** it SHALL derive chunk-index entries from committed large and thin chunk blobs
- **AND** it SHALL NOT require existing chunk-index shard contents to reconstruct those entries

#### Scenario: Full repair can be rerun after interruption
- **WHEN** full chunk-index repair is interrupted after writing only some shard prefixes
- **THEN** rerunning full repair SHALL reconstruct shard contents from committed chunks again
- **AND** it SHALL be able to complete the missing shard prefixes without relying on the partial repair output

#### Scenario: Full repair does not publish snapshot
- **WHEN** full chunk-index repair writes repaired shard blobs
- **THEN** it SHALL NOT create or update any snapshot manifest

## MODIFIED Requirements

### Requirement: Shard merge and flush
The system SHALL collect new chunk index entries during archive and flush updated shards before publishing the snapshot. For each modified shard prefix, `ChunkIndexService` SHALL load the existing shard from cache or remote storage, merge new entries, upload the updated shard, and update local caches. If remote storage has no existing shard for a modified prefix, the service SHALL create a new shard. Flush SHALL process independent shard prefixes with bounded parallelism while ensuring a given prefix is handled by only one worker.

#### Scenario: New entries merged into existing shard
- **WHEN** 50 new files have content hashes with the same shard prefix
- **THEN** the service SHALL load that shard, merge the 50 entries, and upload the updated shard

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

#### Scenario: Flush uses internal worker constant
- **WHEN** `ChunkIndexService.FlushAsync` processes touched shard prefixes
- **THEN** it SHALL bound concurrency using an internal worker-count constant on `ChunkIndexService`

#### Scenario: Partial flush failure does not publish snapshot
- **WHEN** parallel chunk-index flush fails after writing some shard prefixes
- **THEN** the archive operation SHALL fail
- **AND** no snapshot SHALL be published for that failed archive run

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
