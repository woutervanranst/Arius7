## MODIFIED Requirements

### Requirement: ChunkIndexService shared metadata index
The system SHALL provide `ChunkIndexService` in `Arius.Core/Shared/ChunkIndex/` as the single shared facade responsible for content-hash lookup, pending `ShardEntry` recording, shard flush, and in-memory shard-cache invalidation. Chunk metadata lookup SHALL remain separate from chunk blob upload, download, hydration, and cleanup behavior owned by `ChunkStorageService`.

The `ChunkIndexService` implementation SHALL keep read-through shard cache mechanics, read-only lookup behavior, and archive write-session buffering as separate internal responsibilities. The facade SHALL preserve current behavior for callers while delegating those responsibilities to focused internal components.

`ChunkIndexService` SHALL remain the operational boundary for chunk-index behavior. Extracted chunk-index components SHALL be internal implementation details and SHALL NOT be registered as separate DI services or consumed directly by feature handlers, other shared services, CLI code, storage code, or user-facing tests. Architecture tests SHALL enforce that callers outside the chunk-index implementation use `ChunkIndexService` rather than the extracted reader, write-session, or shard cache/store components.

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

#### Scenario: Batched lookup applies the write-session overlay before persisted lookup
- **WHEN** `ChunkIndexService` performs batched lookup for content hashes that include entries recorded in the current archive session
- **THEN** the facade SHALL resolve those session entries before delegating to the read-only reader
- **AND** the read-only reader SHALL receive only content hashes that missed the session overlay
- **AND** the facade SHALL merge session hits with persisted-index results before returning to the caller

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

### Requirement: Explicit full chunk-index repair
The system SHALL provide an explicit full chunk-index repair API and command that rebuilds all chunk-index shards from chunk blobs using the configured shard prefix length. Full repair SHALL purge the local L2 chunk-index cache, scan committed chunk blobs once with `ListAsync("chunks/", includeMetadata: true, ...)`, reconstruct large and thin entries, group reconstructed entries by shard prefix in memory, write rebuilt local L2 shard files for each non-empty prefix, upload every rebuilt non-empty shard to `chunk-index/<prefix>`, and retain the rebuilt L2 files as the current local chunk-index cache. Full repair SHALL NOT write empty shard blobs. Committed chunk blobs are append-only repository data; full repair SHALL treat chunk storage as the durable source for chunk-index reconstruction.

Full repair assumes no concurrent archive or repair operation is mutating the same remote archive. Distributed locking or remote repair leases are out of scope.

Full repair SHALL invalidate chunk-index L1 cache state and mark the local L2 rebuild as in progress before purging or writing local shard-cache files. Normal chunk-index lookups SHALL fail clearly if a previous full repair was interrupted before completing. Full repair SHALL be allowed to run when this marker already exists, and it SHALL purge the partial local rebuild before reconstructing shard contents again. Full repair SHALL clear the in-progress marker only after rebuilt shards have been uploaded and stale remote shards have been deleted.

The repair in-progress marker SHALL be addressed through an internal `ChunkIndexService` constant and SHALL live outside the purgeable local shard-cache directory, for example `~/.arius/{repo}/chunk-index.repair-in-progress`. Chunk-index cache invalidation SHALL NOT delete the repair in-progress marker.

Normal chunk-index operations that trust or mutate chunk-index state, including lookup, entry recording, and pending-entry flush, SHALL check for the repair in-progress marker at operation start and throw `ChunkIndexRepairIncompleteException` when the marker exists. The `ChunkIndexService` constructor SHALL NOT fail solely because the repair in-progress marker exists, so the explicit repair command can construct the service and rerun repair. The explicit full repair API SHALL be allowed to run when the repair in-progress marker already exists. Cache invalidation that only clears local cache state MAY run while the marker exists, but SHALL NOT delete the marker.

Full repair SHALL remember the set of shard prefixes that produced entries during the repair run. After rebuilt shards have been uploaded, full repair SHALL list existing blobs under `chunk-index/` and delete shard blobs whose names are not in that rebuilt prefix set.

Full repair SHALL be idempotent and safe to rerun. If full repair is interrupted while rebuilding local L2 shard files, while uploading rebuilt shards, or after uploading rebuilt shards but before deleting stale remote shard blobs, a later full repair SHALL purge the partial local rebuild and reconstruct shard contents from committed chunks again. Full repair SHALL NOT publish snapshots.

#### Scenario: Full repair rebuilds all touched prefixes
- **WHEN** full chunk-index repair runs on a repository with large and thin chunk blobs
- **THEN** it SHALL reconstruct shard entries for all large and thin chunks
- **AND** it SHALL group reconstructed entries by shard prefix in memory
- **AND** it SHALL write local L2 shard files for every prefix that has reconstructed entries
- **AND** it SHALL upload chunk-index shards for every prefix that has reconstructed entries
- **AND** it SHALL NOT write empty chunk-index shards

#### Scenario: Full repair scans chunks once
- **WHEN** full chunk-index repair runs
- **THEN** it SHALL perform one metadata-aware listing for `chunks/`
- **AND** it SHALL NOT rebuild by issuing one chunk listing per possible shard prefix

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
