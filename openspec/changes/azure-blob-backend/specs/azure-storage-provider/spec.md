## ADDED Requirements

### Requirement: IBlobStorageProvider interface
The system SHALL define an `IBlobStorageProvider` interface in `Arius.Core` that abstracts upload, download, list, delete, and tier-set operations over a blob store. The interface SHALL be independent of any Azure SDK types so that the core handlers do not take a dependency on Azure assemblies.

#### Scenario: Upload blob
- **WHEN** a caller invokes `UploadAsync(blobName, content, tier, ct)`
- **THEN** the blob is stored under the given name with the specified `BlobTier`

#### Scenario: Download blob
- **WHEN** a caller invokes `DownloadAsync(blobName, ct)`
- **THEN** a readable `Stream` of the blob's content is returned

#### Scenario: List blobs by prefix
- **WHEN** a caller invokes `ListAsync(prefix, ct)`
- **THEN** all blob names whose paths begin with `prefix` are yielded as an `IAsyncEnumerable<string>`

#### Scenario: Delete blob
- **WHEN** a caller invokes `DeleteAsync(blobName, ct)`
- **THEN** the blob is removed from the store

#### Scenario: Set blob tier
- **WHEN** a caller invokes `SetTierAsync(blobName, tier, ct)`
- **THEN** the blob's access tier is updated to the specified `BlobTier`

### Requirement: BlobTier enum
The system SHALL define a `BlobTier` enum in `Arius.Core.Models` with values `Hot`, `Cool`, `Cold`, and `Archive`.

#### Scenario: BlobTier values
- **WHEN** code in `Arius.Core` or `Arius.Azure` specifies a storage tier
- **THEN** it uses one of `BlobTier.Hot`, `BlobTier.Cool`, `BlobTier.Cold`, or `BlobTier.Archive`

### Requirement: AzureBlobStorageProvider implements IBlobStorageProvider
The system SHALL provide `AzureBlobStorageProvider` in `Arius.Azure` as the Azure SDK–backed implementation of `IBlobStorageProvider`. It SHALL use `BlobContainerClient` from `Azure.Storage.Blobs` and SHALL NOT expose any Azure SDK types through the `IBlobStorageProvider` surface.

#### Scenario: Upload to Azure
- **WHEN** `UploadAsync` is called with a `BlobTier`
- **THEN** the blob is uploaded to Azure Blob Storage and its access tier is set to the specified tier

#### Scenario: Download from Azure
- **WHEN** `DownloadAsync` is called for an existing blob
- **THEN** the content stream is returned directly from Azure Blob Storage

#### Scenario: List from Azure
- **WHEN** `ListAsync` is called with a prefix
- **THEN** blobs are enumerated from Azure using the prefix filter and their names are yielded

#### Scenario: Delete from Azure
- **WHEN** `DeleteAsync` is called
- **THEN** the blob is deleted from the Azure container

#### Scenario: Set tier on Azure
- **WHEN** `SetTierAsync` is called
- **THEN** the Azure Set Blob Tier API is invoked to update the blob's access tier

### Requirement: Container auto-creation on init
`AzureBlobStorageProvider` SHALL create the target Azure Blob Storage container if it does not already exist when `InitAsync` is called on the owning repository store.

#### Scenario: Container created on first init
- **WHEN** `InitAsync` is called against a non-existent container
- **THEN** the container is created (idempotently) before any blobs are written
