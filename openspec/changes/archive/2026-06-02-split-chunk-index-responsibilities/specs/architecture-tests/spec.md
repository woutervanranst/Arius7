## ADDED Requirements

### Requirement: Chunk-index facade boundary
Architecture tests SHALL enforce that extracted chunk-index implementation components remain behind the `ChunkIndexService` facade. Code outside `Arius.Core.Shared.ChunkIndex` SHALL NOT depend directly on the extracted chunk-index reader, write-session, or shard cache/store components. Feature handlers, DI registration, CLI code, storage code, restore/list/archive workflows, and other shared services SHALL continue to use `ChunkIndexService` as the chunk-index operation boundary. The extracted components SHALL NOT be registered separately in DI.

`ChunkIndexService` SHALL remain public during this responsibility split. The extracted reader, write-session, and shard cache/store components SHALL remain non-public implementation details.

Focused unit tests MAY exercise the extracted internal components directly. User-facing behavior tests SHALL continue to exercise chunk-index behavior through `ChunkIndexService`.

#### Scenario: Extracted components are not consumed outside chunk-index implementation
- **WHEN** architecture tests inspect dependencies on extracted chunk-index reader, write-session, and shard cache/store components
- **THEN** production code outside `Arius.Core.Shared.ChunkIndex` SHALL NOT depend on those components directly
- **AND** feature handlers, DI registration, CLI code, storage code, restore/list/archive workflows, and other shared services SHALL use `ChunkIndexService` as the chunk-index operation boundary
- **AND** service-collection registration code SHALL NOT register those extracted components separately

#### Scenario: Focused component tests may target internals
- **WHEN** tests cover behavior that moved into extracted chunk-index components
- **THEN** focused unit tests MAY construct those internal components directly
- **AND** user-facing behavior tests SHALL continue to cover the behavior through `ChunkIndexService`

#### Scenario: Facade remains public and extracted components remain non-public
- **WHEN** architecture tests inspect chunk-index type visibility
- **THEN** `ChunkIndexService` SHALL be public
- **AND** extracted chunk-index reader, write-session, and shard cache/store components SHALL be non-public
