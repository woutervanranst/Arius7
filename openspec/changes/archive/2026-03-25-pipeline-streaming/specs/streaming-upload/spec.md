## ADDED Requirements

### Requirement: ProgressStream read wrapper
The system SHALL provide a `ProgressStream` class that wraps a readable source stream and reports cumulative bytes read via `IProgress<long>`. The `ProgressStream` SHALL be a read-mode wrapper: it delegates `Read`/`ReadAsync` to the inner stream and reports the cumulative bytes read after each read operation. The total bytes (from `FileInfo.Length`) SHALL be known at construction time, enabling deterministic progress calculation. `ProgressStream` SHALL NOT buffer any data beyond the caller-provided buffer.

#### Scenario: Progress reported on read
- **WHEN** a `ProgressStream` wrapping a 100 MB file is read in 8 KB chunks
- **THEN** the `IProgress<long>` callback SHALL be invoked after each read with the cumulative bytes read so far

#### Scenario: Progress reaches total on completion
- **WHEN** the entire source stream has been read through `ProgressStream`
- **THEN** the final progress report SHALL equal the source file length

#### Scenario: Zero-length file
- **WHEN** a `ProgressStream` wraps a zero-length file
- **THEN** the first read SHALL return 0 bytes and no progress SHALL be reported

### Requirement: CountingStream write wrapper
The system SHALL provide a `CountingStream` class that wraps a writable destination stream and tracks the total bytes written. The `CountingStream` SHALL be a write-mode wrapper: it delegates `Write`/`WriteAsync` to the inner stream and increments a `BytesWritten` property. `BytesWritten` SHALL be readable after the stream is closed to determine the compressed blob size. `CountingStream` SHALL NOT buffer any data.

#### Scenario: Bytes written tracked
- **WHEN** 50 MB of compressed data is written through a `CountingStream`
- **THEN** `BytesWritten` SHALL equal the total bytes written to the inner stream

#### Scenario: BytesWritten available after close
- **WHEN** the `CountingStream` is disposed
- **THEN** `BytesWritten` SHALL remain readable and reflect the final total

### Requirement: Streaming upload chain
The system SHALL upload chunks using a fully streaming chain without in-memory buffering. The chain for large files SHALL be: `ProgressStream(FileStream) -> GZipStream -> EncryptingStream -> CountingStream -> OpenWriteAsync`. The chain for tar bundles SHALL be: `FileStream(tarTempFile) -> GZipStream -> EncryptingStream -> CountingStream -> OpenWriteAsync`. The entire chain SHALL be push-direction (write): data is read from the source and written through each layer to the Azure write stream. No pipes, temp files (except the tar temp file), or `MemoryStream` buffers SHALL be used for the upload itself.

#### Scenario: Large file streaming upload
- **WHEN** a 5 GB file is uploaded
- **THEN** the system SHALL stream it through the chain without allocating a buffer proportional to the file size

#### Scenario: Tar bundle streaming upload
- **WHEN** a sealed tar temp file is uploaded
- **THEN** the system SHALL stream it through gzip -> encrypt -> counting -> OpenWriteAsync without loading the tar into memory

#### Scenario: Encrypted upload content type
- **WHEN** a large file is uploaded with a passphrase
- **THEN** the blob content type SHALL be `application/aes256cbc+gzip`

#### Scenario: Plaintext upload content type
- **WHEN** a large file is uploaded without a passphrase
- **THEN** the blob content type SHALL be `application/gzip`

### Requirement: Metadata written after upload
The system SHALL write blob metadata (`arius-type`, `original-size`, `chunk-size`) via a separate `SetMetadataAsync` call after the upload stream is closed. The `chunk-size` value SHALL be obtained from `CountingStream.BytesWritten`. This preserves the crash-recovery invariant: metadata present means upload is complete. If a crash occurs during upload (before metadata write), the blob SHALL have no metadata and SHALL be overwritten on re-run.

#### Scenario: Metadata set after stream close
- **WHEN** a chunk upload stream is closed successfully
- **THEN** the system SHALL call `SetMetadataAsync` with `arius-type`, `original-size`, and `chunk-size`

#### Scenario: Crash during upload
- **WHEN** a crash occurs while the upload stream is still open
- **THEN** the blob SHALL exist without metadata, and on re-run the system SHALL overwrite it

#### Scenario: Crash between upload and metadata
- **WHEN** a crash occurs after the upload stream closes but before `SetMetadataAsync`
- **THEN** the blob SHALL exist without metadata, and on re-run the system SHALL re-upload (safe, idempotent)
