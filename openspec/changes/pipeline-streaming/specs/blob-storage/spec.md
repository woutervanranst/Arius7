## MODIFIED Requirements

### Requirement: Chunk blob operations
The `IBlobStorageService` SHALL support: upload blob (streaming, with metadata and tier), download blob (streaming), HEAD check (exists + metadata), list blobs by prefix, set blob metadata, copy blob (for rehydration), and open a writable stream for streaming upload. Upload SHALL support setting the access tier (Hot, Cool, Cold, Archive). The `OpenWriteAsync` method SHALL return a writable `Stream` for the specified blob path with the specified content type.

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

### Requirement: Chunk types with blob metadata
Each chunk blob SHALL carry metadata distinguishing its type. The `arius-type` metadata field SHALL be one of: `large`, `tar`, `thin`. Additional metadata: `original-size` (for large and thin), `chunk-size` (compressed blob size for large and tar), `compressed-size` (for thin: proportional estimate within tar). Metadata SHALL be written via `SetMetadataAsync` after the upload stream is closed (not during upload). The `arius-complete` metadata key SHALL NOT be used. The presence of `arius-type` SHALL serve as the sole signal that an upload completed successfully.

#### Scenario: Large chunk metadata
- **WHEN** a large file chunk upload stream is closed and metadata is set
- **THEN** blob metadata SHALL include `arius-type: large`, `original-size: <bytes>`, `chunk-size: <bytes>`

#### Scenario: Thin chunk metadata
- **WHEN** a thin chunk is created for a tar-bundled file
- **THEN** blob metadata SHALL include `arius-type: thin`, `original-size: <bytes>`, `compressed-size: <bytes>`

#### Scenario: Tar chunk metadata
- **WHEN** a tar bundle upload stream is closed and metadata is set
- **THEN** blob metadata SHALL include `arius-type: tar`, `chunk-size: <bytes>`

#### Scenario: No arius-complete key
- **WHEN** any chunk blob metadata is written
- **THEN** the metadata SHALL NOT include an `arius-complete` key
