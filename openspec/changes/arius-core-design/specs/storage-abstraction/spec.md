## ADDED Requirements

### Requirement: Storage provider interface
The system SHALL define a storage provider interface in Core that abstracts all blob storage operations. Core SHALL NOT depend on any Azure-specific types.

#### Scenario: Core uses interface only
- **WHEN** Core orchestrates archive, restore, or ls operations
- **THEN** it SHALL interact with storage exclusively through `IStorageProvider` and related interfaces, with no direct references to Azure SDK types

### Requirement: Blob operations
The storage abstraction SHALL support: upload blob, download blob, check blob existence (HEAD), list blobs by prefix, set blob tier, and initiate rehydration copy.

#### Scenario: Upload a blob
- **WHEN** Core requests uploading content to a blob path
- **THEN** the storage provider SHALL upload the content with the specified content type and tier

#### Scenario: Check blob existence
- **WHEN** Core requests a HEAD check on a blob path
- **THEN** the storage provider SHALL return whether the blob exists and its current tier

#### Scenario: List blobs by prefix
- **WHEN** Core requests listing blobs under a prefix (e.g., `snapshots/`)
- **THEN** the storage provider SHALL return blob names matching the prefix, as `IAsyncEnumerable`

#### Scenario: Initiate rehydration copy
- **WHEN** Core requests rehydration of an archive-tier blob
- **THEN** the storage provider SHALL copy the blob to the rehydration container and return a handle for polling completion

### Requirement: Container prefix layout
The storage provider SHALL organize blobs under well-known prefixes: `chunks/`, `trees/`, `snapshots/`, `chunk-index/`, `chunks-rehydrated/`.

#### Scenario: Blob paths use correct prefixes
- **WHEN** a chunk is uploaded
- **THEN** its blob path SHALL be `chunks/<hash>`

#### Scenario: Tree blob path
- **WHEN** a tree node is uploaded
- **THEN** its blob path SHALL be `trees/<tree-hash>.enc`

### Requirement: Backend replaceability
The storage abstraction SHALL be designed such that a new backend (e.g., S3, local filesystem) can be implemented by providing a new `IStorageProvider` implementation without changes to Core.

#### Scenario: Alternative backend
- **WHEN** a new storage backend is implemented
- **THEN** it SHALL only need to implement the `IStorageProvider` interface and be registered via dependency injection
