## MODIFIED Requirements

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
