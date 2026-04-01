# Container Names Spec

## Purpose

Defines the account-scoped `ContainerNamesQuery` used via Mediator to stream Arius repository container names from an Azure Blob Storage account.

## Requirements

### Requirement: List container names from storage account
The system SHALL provide a streaming query (`IStreamQuery<string>`) that lists Arius container (repository) names from a single Azure Blob Storage account. The query SHALL accept storage account connection parameters and return container names as `IAsyncEnumerable<string>` via `IStreamQueryHandler`. Only containers that contain valid Arius data (that is, have the expected blob prefix `snapshots/`) SHALL be included. The check SHALL use a single `ListBlobs` call with prefix and `maxResults=1` to be efficient. Consumers SHALL execute the query through Mediator streaming.

#### Scenario: List containers from storage account
- **WHEN** the `ContainerNamesQuery` is executed with a valid storage account connection
- **THEN** the system SHALL return the names of all containers that contain Arius repository data as a stream

#### Scenario: Empty storage account
- **WHEN** the `ContainerNamesQuery` is executed against a storage account with no Arius containers
- **THEN** the system SHALL return an empty sequence

#### Scenario: Mixed containers
- **WHEN** the storage account contains both Arius containers and non-Arius containers (e.g., `$logs`, `$web`)
- **THEN** the system SHALL only return the names of containers that contain Arius data

#### Scenario: Streaming consumption
- **WHEN** the Explorer consumes the `ContainerNamesQuery` via `mediator.CreateStream(query)`
- **THEN** container names SHALL appear progressively as each container is validated
