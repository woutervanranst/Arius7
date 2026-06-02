## MODIFIED Requirements

### Requirement: ChunkIndexService shared metadata index
The system SHALL provide `ChunkIndexService` in `Arius.Core/Shared/ChunkIndex/` as the single shared facade responsible for content-hash lookup, pending `ShardEntry` recording, shard flush, and in-memory shard-cache invalidation. Chunk metadata lookup SHALL remain separate from chunk blob upload, download, hydration, and cleanup behavior owned by `ChunkStorageService`.

The `ChunkIndexService` implementation SHALL keep read-through shard cache mechanics, read-only lookup behavior, and archive write-session buffering as separate internal responsibilities. The facade SHALL preserve current behavior for callers while delegating those responsibilities to focused internal components.

`ChunkIndexService` SHALL remain the operational boundary for chunk-index behavior. Extracted chunk-index components SHALL be internal implementation details and SHALL NOT be registered as separate DI services or consumed directly by feature handlers, other shared services, CLI code, storage code, or user-facing tests. Architecture tests SHALL enforce that callers outside the chunk-index implementation use `ChunkIndexService` rather than the extracted reader, write-session, or shard cache/store components.

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

#### Scenario: Facade boundary is enforced by architecture tests
- **WHEN** architecture tests inspect dependencies on extracted chunk-index reader, write-session, and shard cache/store components
- **THEN** code outside the chunk-index implementation SHALL NOT depend on those components directly
- **AND** feature handlers, DI registration, CLI code, storage code, restore/list/archive workflows, and other shared services SHALL use `ChunkIndexService` as the chunk-index operation boundary

#### Scenario: New components have focused coverage
- **WHEN** tests are run with coverage for `src/Arius.Core/Shared/ChunkIndex/`
- **THEN** line coverage SHALL be greater than 90%
- **AND** the coverage SHALL include focused tests for the extracted shard cache/store, read-only reader, and write-session responsibilities

#### Scenario: Existing tests are split by responsibility
- **WHEN** the current `ChunkIndexService` tests are updated for the split
- **THEN** facade behavior SHALL remain covered through `ChunkIndexService` tests
- **AND** implementation-detail behavior that moved into extracted components SHALL be covered by focused component tests rather than one broad service test file
