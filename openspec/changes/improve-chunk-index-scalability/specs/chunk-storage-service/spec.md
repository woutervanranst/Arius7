## MODIFIED Requirements

### Requirement: Chunk storage upload API
`ChunkStorageService` SHALL expose separate asynchronous methods for large chunks, tar chunks, and thin chunks:

- `UploadLargeAsync(string chunkHash, Stream content, long sourceSize, BlobTier tier, IProgress<long>? progress, CancellationToken)`
- `UploadTarAsync(string chunkHash, Stream content, long sourceSize, BlobTier tier, IProgress<long>? progress, CancellationToken)`
- `UploadThinAsync(string contentHash, string parentChunkHash, long originalSize, long compressedSize, CancellationToken)`

`UploadLargeAsync` and `UploadTarAsync` SHALL return a `ChunkUploadResult` containing the chunk hash, stored size, and whether the blob already existed. `UploadThinAsync` SHALL return `true` when it creates the thin chunk blob and `false` when a committed thin chunk already exists.

#### Scenario: Large chunk upload returns stored size
- **WHEN** a large chunk is uploaded through `UploadLargeAsync`
- **THEN** the method SHALL return the chunk hash and the stored chunk size after gzip and optional encryption are applied

#### Scenario: Tar chunk upload uses explicit tar method
- **WHEN** a sealed tar bundle is uploaded
- **THEN** the feature SHALL call `UploadTarAsync` rather than a generic kind-switching upload API

#### Scenario: Thin chunk upload uses parent chunk hash
- **WHEN** a tar-bundled file needs its thin chunk blob created
- **THEN** the feature SHALL call `UploadThinAsync` with the file content hash and the tar chunk's `parentChunkHash`

#### Scenario: Thin chunk upload stores parent hash in metadata
- **WHEN** `UploadThinAsync` creates a thin chunk blob
- **THEN** `ChunkStorageService` SHALL upload an empty blob body to `chunks/<content-hash>`
- **AND** it SHALL include metadata `arius_type: thin`, `parent_chunk_hash`, `original_size`, and `compressed_size` on that upload
- **AND** `parent_chunk_hash` SHALL contain the parent tar chunk hash passed to `UploadThinAsync`

#### Scenario: Existing committed thin chunk is accepted
- **WHEN** `UploadThinAsync` encounters an existing thin chunk blob
- **AND** metadata contains `arius_type`
- **THEN** `UploadThinAsync` SHALL return `false` without rewriting the blob

#### Scenario: Existing uncommitted thin chunk is retried
- **WHEN** `UploadThinAsync` encounters an existing thin chunk blob
- **AND** metadata does not contain `arius_type`
- **THEN** `ChunkStorageService` SHALL treat the blob as incomplete
- **AND** it SHALL delete and retry thin chunk creation

### Requirement: Chunk storage owns storage transforms and metadata protocol
`ChunkStorageService` SHALL own the chunk storage encoding and decoding protocol. `UploadLargeAsync` and `UploadTarAsync` SHALL accept plaintext source streams and SHALL internally apply optional progress reporting, gzip compression, encryption, stored-size counting, blob upload, metadata writes, tier assignment, and create-if-not-exists crash-recovery rules. `UploadThinAsync` SHALL own thin chunk blob naming, empty-body upload with required metadata, and create-if-not-exists crash-recovery rules. `DownloadAsync(string chunkHash, IProgress<long>? progress, CancellationToken)` SHALL return a plaintext readable stream and SHALL internally choose the best readable blob source, apply optional progress reporting, decrypt, and gunzip before returning the stream.

Feature handlers SHALL NOT construct chunk blob names, select chunk content types, write chunk metadata keys, or build the gzip/encryption stream chain themselves.

#### Scenario: Archive handler does not build upload protocol
- **WHEN** `ArchiveCommandHandler` uploads a large, tar, or thin chunk
- **THEN** it SHALL pass upload inputs to `ChunkStorageService`
- **AND** it SHALL NOT directly call `BlobPaths.ChunkPath(...)` or `BlobPaths.ThinChunkPath(...)`, choose chunk content types, write chunk metadata, or construct thin chunk bodies

#### Scenario: Restore handler receives plaintext download stream
- **WHEN** `RestoreCommandHandler` downloads a chunk through `DownloadAsync`
- **THEN** the returned stream SHALL already be decrypted and gunzipped plaintext suitable for direct large-file restore or tar extraction

#### Scenario: Chunk storage handles already-exists recovery
- **WHEN** a chunk upload encounters a previously existing blob at the target name
- **THEN** `ChunkStorageService` SHALL use `arius_type` as the committed-blob sentinel and perform the existing recover-or-delete-and-retry behavior internally
