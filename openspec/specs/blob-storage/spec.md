# Blob Storage Spec

## Purpose

Defines the blob storage abstraction layer, container layout, and rehydration strategy for Arius. Arius.Core depends only on the `IBlobStorageService` interface; the Azure implementation lives in Arius.AzureBlob.

## Requirements

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
The `IBlobStorageService` SHALL support: upload blob (streaming, with metadata and tier), download blob (streaming), HEAD check (exists + metadata), list blobs by prefix, optionally include metadata in blob listing results, set blob metadata, copy blob (for rehydration), and open a writable stream for streaming upload. Upload SHALL support setting the access tier (Hot, Cool, Cold, Archive). The `OpenWriteAsync` method SHALL return a writable `Stream` for the specified blob path with the specified content type. `OpenWriteAsync` SHALL use `IfNoneMatch=*` (create-if-not-exists) semantics: if a blob already exists at the target path, it SHALL throw `BlobAlreadyExistsException` immediately before any data is written.

`ListAsync(RelativePath prefix, bool includeMetadata = false, CancellationToken cancellationToken = default)` SHALL return blob list items containing at least the blob name. When `includeMetadata` is false, implementations MAY omit metadata and content properties. When `includeMetadata` is true, implementations SHALL populate metadata and available content information from the listing operation where the backend supports it.

Thin chunk metadata SHALL include `parent_chunk_hash` to identify the parent tar chunk hash for that content hash. Metadata-aware listing SHALL expose this metadata when the backend provides blob metadata in listing results.

#### Scenario: Upload large chunk
- **WHEN** uploading a gzip+encrypted stream to `chunks/<hash>`
- **THEN** the service SHALL upload the stream with specified metadata and tier

#### Scenario: HEAD check for crash recovery
- **WHEN** checking if `chunks/<hash>` exists
- **THEN** the service SHALL return existence, blob metadata (including `arius_type`), and blob tier

#### Scenario: Download for restore
- **WHEN** downloading `chunks/<hash>` or `chunks-rehydrated/<hash>`
- **THEN** the service SHALL return a readable stream

#### Scenario: List names only
- **WHEN** `ListAsync(prefix, includeMetadata: false)` is called
- **THEN** each returned item SHALL include the blob name
- **AND** callers SHALL NOT require metadata to be populated

#### Scenario: List with metadata
- **WHEN** `ListAsync(prefix, includeMetadata: true)` is called
- **THEN** each returned item SHALL include the blob name and available metadata from the listing operation
- **AND** repair workflows SHALL be able to inspect `arius_type` and thin chunk `parent_chunk_hash` without issuing a separate HEAD request per listed blob when the backend supports metadata listing

#### Scenario: Thin chunk parent metadata listed
- **WHEN** `ListAsync(ChunkPrefix, includeMetadata: true)` returns a thin chunk blob
- **THEN** the listed blob item SHALL expose `parent_chunk_hash` metadata when the backend listing provides metadata

#### Scenario: OpenWriteAsync returns writable stream
- **WHEN** `OpenWriteAsync("chunks/<hash>", contentType, tier)` is called
- **THEN** the service SHALL return a writable `Stream` that uploads data to Azure as it is written, using `BlockBlobClient.OpenWriteAsync()` in the Azure implementation

#### Scenario: OpenWriteAsync with access tier
- **WHEN** `OpenWriteAsync` is called with tier Archive
- **THEN** the blob SHALL be created in Archive tier

#### Scenario: OpenWriteAsync throws BlobAlreadyExistsException
- **WHEN** `OpenWriteAsync(blobName, …)` is called and a blob already exists at that path
- **THEN** the service SHALL throw `BlobAlreadyExistsException` immediately (before any data is written)

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
