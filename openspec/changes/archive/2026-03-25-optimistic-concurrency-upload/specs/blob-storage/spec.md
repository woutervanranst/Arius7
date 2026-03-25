## MODIFIED Requirements

### Requirement: BlobAlreadyExistsException
The system SHALL define `BlobAlreadyExistsException : IOException` (sealed) with a `BlobName : string` property. This exception is thrown by `OpenWriteAsync` and `UploadAsync(overwrite:false)` when a blob already exists at the target name, providing a stable contract for callers to handle concurrency conflicts without catching raw `RequestFailedException`.

The Azure implementation SHALL map both HTTP 412 ConditionNotMet (real Azure, `IfNoneMatch=*` condition) and HTTP 409 BlobAlreadyExists (Azurite emulator behaviour for `OpenWriteAsync`) to `BlobAlreadyExistsException`.

Scenarios:
- WHEN `OpenWriteAsync(blobName, …)` is called and a blob already exists at that path, THEN the service SHALL throw `BlobAlreadyExistsException` immediately (before any data is written)
- WHEN `UploadAsync(blobName, …, overwrite: false)` is called and the blob already exists, THEN the service SHALL throw `BlobAlreadyExistsException`
- WHEN `UploadAsync(blobName, …, overwrite: true)` is called, THEN the service SHALL overwrite unconditionally and SHALL NOT throw `BlobAlreadyExistsException`

### Requirement: Chunk blob operations
The `IBlobStorageService` SHALL support: upload blob (streaming, with metadata and tier), download blob (streaming), HEAD check (exists + metadata), list blobs by prefix, set blob metadata, copy blob (for rehydration), and open a writable stream for streaming upload. Upload SHALL support setting the access tier (Hot, Cool, Cold, Archive). The `OpenWriteAsync` method SHALL return a writable `Stream` for the specified blob path with the specified content type. `OpenWriteAsync` SHALL use `IfNoneMatch=*` (create-if-not-exists) semantics: if a blob already exists at the target path, it SHALL throw `BlobAlreadyExistsException` immediately before any data is written.

#### Scenario: Upload large chunk
- **WHEN** uploading a gzip+encrypted stream to `chunks/<hash>`
- **THEN** the service SHALL upload the stream with specified metadata and tier

#### Scenario: HEAD check for crash recovery
- **WHEN** checking if `chunks/<hash>` exists
- **THEN** the service SHALL return existence, blob metadata (including `arius-type`), and blob tier

#### Scenario: Download for restore
- **WHEN** downloading `chunks/<hash>` or `chunks-rehydrated/<hash>`
- **THEN** the service SHALL return a readable stream

#### Scenario: OpenWriteAsync returns writable stream
- **WHEN** `OpenWriteAsync("chunks/<hash>", contentType, tier)` is called
- **THEN** the service SHALL return a writable `Stream` that uploads data to Azure as it is written, using `BlockBlobClient.OpenWriteAsync()` in the Azure implementation

#### Scenario: OpenWriteAsync with access tier
- **WHEN** `OpenWriteAsync` is called with tier Archive
- **THEN** the blob SHALL be created in Archive tier

#### Scenario: OpenWriteAsync throws BlobAlreadyExistsException
- **WHEN** `OpenWriteAsync(blobName, …)` is called and a blob already exists at that path
- **THEN** the service SHALL throw `BlobAlreadyExistsException` immediately (before any data is written)
