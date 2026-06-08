# Blob Storage Spec

## Purpose

Defines the blob storage abstraction layer, container layout, and rehydration strategy for Arius. Arius.Core depends only on the `IBlobContainerService` interface; the Azure implementation lives in Arius.AzureBlob.
## Requirements
### Requirement: Blob storage abstraction
The system SHALL define an `IBlobContainerService` interface in Arius.Core that abstracts all blob storage operations, including container creation. Arius.Core SHALL NOT reference Azure.Storage.Blobs or any Azure-specific types. The Azure implementation (`Arius.AzureBlob`) SHALL implement this interface. The interface SHALL include a `CreateContainerIfNotExistsAsync` method that ensures the blob container exists before any blob operations are performed.

#### Scenario: Core has no Azure dependency
- **WHEN** Arius.Core is built
- **THEN** it SHALL compile without any reference to Azure.Storage.Blobs

#### Scenario: Alternative backend
- **WHEN** a new storage backend (e.g., S3) is needed in the future
- **THEN** it SHALL be implementable by providing a new `IBlobContainerService` implementation without modifying Core

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

### Requirement: BlobAlreadyExistsException
The system SHALL define `BlobAlreadyExistsException : IOException` (sealed) with a `BlobName : string` property. This exception is thrown by `OpenWriteAsync` and `UploadAsync(overwrite:false)` when a blob already exists at the target name, providing a stable contract for callers to handle concurrency conflicts without catching raw `RequestFailedException`.

The Azure implementation SHALL map both HTTP 412 ConditionNotMet (real Azure, `IfNoneMatch=*` condition) and HTTP 409 BlobAlreadyExists (Azurite emulator behaviour for `OpenWriteAsync`) to `BlobAlreadyExistsException`.

#### Scenario: OpenWriteAsync throws BlobAlreadyExistsException on existing blob
- **WHEN** `OpenWriteAsync(blobName, …)` is called and a blob already exists at that path
- **THEN** the service SHALL throw `BlobAlreadyExistsException` immediately (before any data is written)

#### Scenario: UploadAsync overwrite:false throws BlobAlreadyExistsException
- **WHEN** `UploadAsync(blobName, …, overwrite: false)` is called and the blob already exists
- **THEN** the service SHALL throw `BlobAlreadyExistsException`

#### Scenario: UploadAsync overwrite:true succeeds unconditionally
- **WHEN** `UploadAsync(blobName, …, overwrite: true)` is called
- **THEN** the service SHALL overwrite unconditionally and SHALL NOT throw `BlobAlreadyExistsException`

### Requirement: Chunk blob operations
The `IBlobContainerService` SHALL support: upload blob (streaming, with metadata and tier), download blob (streaming), HEAD check (exists + metadata + opaque blob identity), list blobs by prefix, optionally include metadata and opaque blob identity in blob listing results, set blob metadata, copy blob (for rehydration), and open a writable stream for streaming upload. Upload and copy operations SHALL support setting the access tier (Hot, Cool, Cold, Archive). The `OpenWriteAsync` method SHALL return a writable `Stream` for the specified blob path with the specified content type. `OpenWriteAsync` SHALL use `IfNoneMatch=*` (create-if-not-exists) semantics: if a blob already exists at the target path, it SHALL throw `BlobAlreadyExistsException` immediately before any data is written.

The opaque blob identity SHALL be exposed as a string property named `ETag` on the result records that carry it (`UploadResult`, `DownloadResult`, `BlobMetadata`, `BlobListItem`). It SHALL be suitable for detecting whether a blob body changed between two observations. Azure implementations SHALL populate this value from the blob ETag. `GetMetadataAsync` SHALL populate `BlobMetadata.ETag` for existing blobs when the backend provides one. `ListAsync(prefix, includeMetadata: true, ...)` SHALL populate `BlobListItem.ETag` for listed blobs when the backend listing returns it. `DownloadAsync`/`TryDownloadAsync` SHALL populate `DownloadResult.ETag`. Core callers SHALL treat the identity as an opaque string (string equality only) and SHALL NOT parse Azure-specific ETag syntax.

`UploadAsync` SHALL return an `UploadResult` exposing the resulting `ETag` for the uploaded blob, so callers that need the new blob identity can update local validation state without an extra HEAD request. When a blob is uploaded through `OpenWriteAsync`, callers that need the resulting blob identity SHALL call `GetMetadataAsync` after the returned write stream has been closed successfully. Chunk-index flush uses `UploadAsync` and reads the identity directly from `UploadResult.ETag`; chunk-index prefix validation reads `DownloadResult.ETag` from `TryDownloadAsync` rather than issuing a separate HEAD.

#### Scenario: Upload returns blob identity
- **WHEN** `UploadAsync` uploads a blob successfully
- **THEN** it SHALL return an `UploadResult` for the uploaded blob
- **AND** the result SHALL include the opaque `ETag` identity when the backend provides one

#### Scenario: OpenWrite callers fetch resulting identity
- **WHEN** a caller uploads a blob through `OpenWriteAsync`
- **AND** the caller needs the uploaded blob identity
- **THEN** the caller SHALL close the returned write stream successfully
- **AND** it SHALL call `GetMetadataAsync` for the uploaded blob

#### Scenario: HEAD returns blob identity
- **WHEN** `GetMetadataAsync` checks an existing blob
- **THEN** the returned `BlobMetadata` SHALL include an opaque `ETag` identity when the backend provides one
- **AND** Azure-backed metadata SHALL use the blob ETag as that identity

#### Scenario: Download returns blob identity
- **WHEN** `DownloadAsync` or `TryDownloadAsync` returns a blob stream
- **THEN** the returned `DownloadResult` SHALL include the opaque `ETag` identity when the backend provides one

#### Scenario: Metadata listing returns blob identity
- **WHEN** `ListAsync(prefix, includeMetadata: true)` returns blob items
- **THEN** each returned item SHALL include an opaque `ETag` identity when the backend listing provides one

#### Scenario: Core treats blob identity as opaque
- **WHEN** Core compares two observations of the same blob
- **THEN** it SHALL compare the blob identity as an opaque string
- **AND** it SHALL NOT parse Azure-specific ETag syntax

### Requirement: Chunk types with blob metadata
Each chunk blob SHALL carry metadata distinguishing its type. The `arius_type` metadata field SHALL be one of: `large`, `tar`, `thin`. Additional metadata: `original_size` (for large and thin), `chunk_size` (compressed blob size for large and tar), `compressed_size` (for thin: proportional estimate within tar), and `parent_chunk_hash` (for thin). The `arius_complete` metadata key SHALL NOT be used. The presence of `arius_type` SHALL serve as the sole signal that an upload completed successfully.

#### Scenario: Large chunk metadata
- **WHEN** a large file chunk upload stream is closed and metadata is set
- **THEN** blob metadata SHALL include `arius_type: large`, `original_size: <bytes>`, `chunk_size: <bytes>`

#### Scenario: Thin chunk metadata
- **WHEN** a thin chunk is created for a tar-bundled file
- **THEN** blob metadata SHALL include `arius_type: thin`, `parent_chunk_hash: <tar-chunk-hash>`, `original_size: <bytes>`, `compressed_size: <bytes>`

#### Scenario: Tar chunk metadata
- **WHEN** a tar bundle upload stream is closed and metadata is set
- **THEN** blob metadata SHALL include `arius_type: tar`, `chunk_size: <bytes>`

#### Scenario: No arius_complete key
- **WHEN** any chunk blob metadata is written
- **THEN** the metadata SHALL NOT include an `arius_complete` key

### Requirement: Container layout
The blob storage SHALL organize blobs into the following virtual directories: `chunks/` (configurable tier), `chunks-rehydrated/` (Hot tier, temporary), `filetrees/` (Cool tier), `snapshots/` (Cool tier), `chunk-index/` (Cool tier).

Filetree blobs SHALL use content type `application/aes256cbc+gzip` when encrypted or `application/gzip` when not encrypted, matching the chunk index and snapshot content type convention.

#### Scenario: Chunk stored in correct path
- **WHEN** a large file with hash `abc123` is uploaded
- **THEN** the blob SHALL be at `chunks/abc123`

#### Scenario: Rehydrated chunk path
- **WHEN** a chunk is rehydrated for restore
- **THEN** the rehydrated copy SHALL be at `chunks-rehydrated/<chunk-hash>`

#### Scenario: Encrypted tree blob content type
- **WHEN** a tree blob is uploaded with a passphrase
- **THEN** the blob SHALL be at `filetrees/<hash>` with content type `application/aes256cbc+gzip`

#### Scenario: Plaintext tree blob content type
- **WHEN** a tree blob is uploaded without a passphrase
- **THEN** the blob SHALL be at `filetrees/<hash>` with content type `application/gzip`

### Requirement: Per-repository cache identification
The local cache SHALL be organized by repository using a human-readable directory name: `{accountName}-{containerName}`. The cache path SHALL be `~/.arius/{accountName}-{containerName}/`. Azure account names (`[a-z0-9]{3,24}`) do not contain hyphens, so the first hyphen in the directory name unambiguously separates account from container. The `ComputeRepoId` hash function SHALL be removed.

#### Scenario: Two repositories on same machine
- **WHEN** archiving to account `mystorageacct` container `photos` and account `otherstorage` container `backups`
- **THEN** the cache directories SHALL be `~/.arius/mystorageacct-photos/` and `~/.arius/otherstorage-backups/`

#### Scenario: Directory name is human-readable
- **WHEN** listing `~/.arius/`
- **THEN** each subdirectory name SHALL clearly identify the account and container it belongs to

#### Scenario: Account with multiple containers
- **WHEN** archiving to account `myacct` with containers `photos` and `documents`
- **THEN** the cache directories SHALL be `~/.arius/myacct-photos/` and `~/.arius/myacct-documents/`

### Requirement: Tree blob caching
Tree blobs SHALL be cached on local disk at `~/.arius/{accountName}-{containerName}/filetrees/` and valid indefinitely (content-addressed = immutable). The cache SHALL be used for `ls` and `restore` tree traversal.

#### Scenario: Cached tree blob reuse
- **WHEN** a tree blob was downloaded during a previous `ls` or `restore`
- **THEN** subsequent operations SHALL use the cached version without contacting Azure

#### Scenario: Cache on mounted volume in Docker
- **WHEN** running in Docker
- **THEN** the cache directory (`~/.arius/`) SHALL be on a mounted volume to persist across container restarts

### Requirement: Rehydration via blob copy
For restore, archive-tier chunks SHALL be rehydrated by copying to `chunks-rehydrated/` in Hot tier (not rehydrated in place). The copy operation SHALL specify the rehydration priority (Standard or High). The system SHALL check for existing rehydrated copies before initiating new rehydration.

#### Scenario: Rehydrate to Hot tier
- **WHEN** chunk `abc123` needs rehydration with Standard priority
- **THEN** the system SHALL issue a copy-blob from `chunks/abc123` to `chunks-rehydrated/abc123` with rehydrate priority Standard

#### Scenario: Already rehydrated
- **WHEN** `chunks-rehydrated/abc123` already exists
- **THEN** the system SHALL skip rehydration and use the existing copy

