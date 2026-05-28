## ADDED Requirements

### Requirement: Fixed shard prefix layout
The chunk index SHALL derive shard prefixes from one internal repository-wide prefix-length constant. All chunk-index lookup, flush, repair, local cache paths, and remote shard paths SHALL use that same prefix length. The prefix length SHALL NOT be configurable through the CLI in this change.

#### Scenario: Prefix calculation uses layout constant
- **WHEN** the chunk index calculates the shard prefix for a content hash
- **THEN** it SHALL take the first `ChunkIndexLayout.ShardPrefixLength` hex characters from the content hash

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
Prefix repair SHALL rebuild a chunk-index shard by listing chunk blobs whose names start with `chunks/<prefix>`. Large chunk blobs SHALL reconstruct entries where content hash equals chunk hash. Thin chunk blobs SHALL reconstruct entries where content hash maps to the tar chunk hash stored in the thin chunk body. Tar chunk blobs SHALL NOT directly create chunk-index entries because thin chunks are the per-file mapping source.

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

### Requirement: Explicit full chunk-index repair
The system SHALL provide an explicit full chunk-index repair API and command that rebuilds all chunk-index shards from chunk blobs using the configured shard prefix length. Full repair SHALL enumerate all chunk blobs, reconstruct large and thin entries, group them by shard prefix, and write complete shard blobs under `chunk-index/`.

#### Scenario: Full repair rebuilds all touched prefixes
- **WHEN** full chunk-index repair runs on a repository with large and thin chunk blobs
- **THEN** it SHALL reconstruct shard entries for all large and thin chunks
- **AND** it SHALL upload chunk-index shards for every prefix that has reconstructed entries

#### Scenario: Full repair available for maintenance
- **WHEN** an operator invokes the full chunk-index repair command
- **THEN** the command SHALL rebuild the remote chunk index from chunk blobs without requiring archive reruns

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

### Requirement: Mutable shard cache invalidation
Chunk index shards are mutable repository metadata. When archive cache coordination shows that another machine may have updated the repository, the system SHALL invalidate stale chunk index cache state by deleting all files in `~/.arius/{repo}/chunk-index/` and calling `ChunkIndexService.InvalidateL1()` before future lookups trust cached shard contents. Chunk-index invalidation SHALL be owned by `ChunkIndexService` and coordinated by the archive workflow, not by `FileTreeService`.

#### Scenario: Snapshot mismatch invalidates chunk index cache
- **WHEN** a snapshot mismatch is detected during archive cache coordination
- **THEN** all files in `~/.arius/{repo}/chunk-index/` SHALL be deleted
- **AND** `ChunkIndexService.InvalidateL1()` SHALL be called

#### Scenario: Next lookup reloads from remote storage
- **WHEN** the chunk index cache was invalidated after a snapshot mismatch
- **THEN** the next lookup SHALL re-download needed shards from remote storage instead of trusting stale local cache data

#### Scenario: FileTreeService does not invalidate chunk index
- **WHEN** `FileTreeService` validates or materializes filetree cache state
- **THEN** it SHALL NOT delete chunk-index cache files
- **AND** it SHALL NOT call `ChunkIndexService.InvalidateL1()`
