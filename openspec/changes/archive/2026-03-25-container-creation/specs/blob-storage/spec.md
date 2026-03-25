## MODIFIED Requirements

### Requirement: Blob storage abstraction
The system SHALL define an `IBlobStorageService` interface in Arius.Core that abstracts all blob storage operations, including container creation. Arius.Core SHALL NOT reference Azure.Storage.Blobs or any Azure-specific types. The Azure implementation (`Arius.AzureBlob`) SHALL implement this interface. The interface SHALL include a `CreateContainerIfNotExistsAsync` method that ensures the blob container exists before any blob operations are performed.

#### Scenario: Core has no Azure dependency
- **WHEN** Arius.Core is built
- **THEN** it SHALL compile without any reference to Azure.Storage.Blobs

#### Scenario: Alternative backend
- **WHEN** a new storage backend (e.g., S3) is needed in the future
- **THEN** it SHALL be implementable by providing a new `IBlobStorageService` implementation without modifying Core

#### Scenario: Container creation at startup
- **WHEN** the archive or restore pipeline handler starts
- **THEN** it SHALL call `CreateContainerIfNotExistsAsync` before performing any blob operations

#### Scenario: Container already exists
- **WHEN** `CreateContainerIfNotExistsAsync` is called and the container already exists
- **THEN** the call SHALL succeed as a no-op (idempotent)

#### Scenario: Container does not exist
- **WHEN** `CreateContainerIfNotExistsAsync` is called and the container does not exist
- **THEN** the system SHALL create the container and proceed normally

#### Scenario: First-time user with new container
- **WHEN** a user runs `arius archive` against a container that does not exist
- **THEN** the container SHALL be automatically created instead of crashing with an unhandled exception
