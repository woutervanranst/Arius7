## MODIFIED Requirements

### Requirement: Chunk blob operations
The `IBlobStorageService` SHALL support: upload blob (streaming, with metadata and tier), download blob (streaming), HEAD check (exists + metadata), list blobs by prefix, optionally include metadata in blob listing results, set blob metadata, copy blob (for rehydration), and open a writable stream for streaming upload. Upload SHALL support setting the access tier (Hot, Cool, Cold, Archive). The `OpenWriteAsync` method SHALL return a writable `Stream` for the specified blob path with the specified content type. `OpenWriteAsync` SHALL use `IfNoneMatch=*` (create-if-not-exists) semantics: if a blob already exists at the target path, it SHALL throw `BlobAlreadyExistsException` immediately before any data is written.

`ListAsync(RelativePath prefix, bool includeMetadata = false, CancellationToken cancellationToken = default)` SHALL return blob list items containing at least the blob name. When `includeMetadata` is false, implementations MAY omit metadata and content properties. When `includeMetadata` is true, implementations SHALL populate metadata and available content information from the listing operation where the backend supports it.

Thin chunk metadata SHALL include `parent_chunk_hash` to identify the parent tar chunk hash for that content hash. Metadata-aware listing SHALL expose this metadata when the backend provides blob metadata in listing results.

#### Scenario: Upload large chunk
- **WHEN** uploading a gzip+encrypted stream to `chunks/<hash>`
- **THEN** the service SHALL upload the stream with specified metadata and tier

#### Scenario: HEAD check for crash recovery
- **WHEN** checking if `chunks/<hash>` exists
- **THEN** the service SHALL return existence, blob metadata (including `arius-type`), and blob tier

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
- **AND** repair workflows SHALL be able to inspect `arius-type` and thin chunk `parent_chunk_hash` without issuing a separate HEAD request per listed blob when the backend supports metadata listing

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
