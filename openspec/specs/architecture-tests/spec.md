# Architecture Tests Spec

## Purpose

Defines the architecture test suite that enforces dependency boundaries between Arius projects, ensuring Azure SDK namespaces are only used within `Arius.AzureBlob`.
## Requirements
### Requirement: Only Arius.AzureBlob may depend on Azure SDK namespaces
The architecture tests SHALL enforce that only the `Arius.AzureBlob` assembly may depend on types in Azure SDK namespaces. Specifically, `Arius.Core` and `Arius.Cli` SHALL NOT depend on types in `Azure.Storage`, `Azure.Identity`, `Azure.Core`, or any other `Azure.*` namespace. The existing `Core_Should_Not_Reference_Azure` test SHALL be widened from checking only `Azure.Storage` to checking all `Azure` namespaces. A new `Cli_Should_Not_Reference_Azure` test SHALL be added to enforce the same boundary for the CLI project.

#### Scenario: Core has no Azure dependency
- **WHEN** the architecture tests run against `Arius.Core`
- **THEN** no class in `Arius.Core` SHALL depend on types in any `Azure` namespace (including `Azure.Storage`, `Azure.Identity`, `Azure.Core`)

#### Scenario: CLI has no Azure dependency
- **WHEN** the architecture tests run against `Arius.Cli`
- **THEN** no class in `Arius.Cli` SHALL depend on types in any `Azure` namespace (including `Azure.Storage`, `Azure.Identity`, `Azure.Core`)

#### Scenario: AzureBlob may depend on Azure namespaces
- **WHEN** the architecture tests run against `Arius.AzureBlob`
- **THEN** classes in `Arius.AzureBlob` SHALL be permitted to depend on `Azure.Storage`, `Azure.Identity`, and `Azure.Core` types

#### Scenario: Future Azure dependency in CLI caught by test
- **WHEN** a developer adds a `using Azure.Storage.Blobs` to a file in `Arius.Cli`
- **THEN** the `Cli_Should_Not_Reference_Azure` architecture test SHALL fail

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

