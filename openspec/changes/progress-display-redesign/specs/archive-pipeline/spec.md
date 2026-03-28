## MODIFIED Requirements

### Requirement: File enumeration
The system SHALL recursively enumerate all files in the local root directory, producing FilePair units for archiving using a single-pass streaming approach. Files with the `.pointer.arius` suffix SHALL always be treated as pointer files. All other files SHALL be treated as binary files. If a file cannot be read (e.g., system-protected), the system SHALL log a warning and continue with the remaining files. Enumeration SHALL be depth-first to provide directory affinity for the tar builder. Enumeration SHALL yield FilePair objects immediately as files are discovered without materializing the full file list into memory. When encountering a binary file, the system SHALL check `File.Exists(binaryPath + ".pointer.arius")` to pair it. When encountering a pointer file, the system SHALL check `File.Exists(pointerPath[..^".pointer.arius".Length])` -- if the binary exists, skip (already emitted with the binary); if not, yield as pointer-only. No dictionaries or state tracking SHALL be used.

During enumeration, the system SHALL publish a `FileScannedEvent(string RelativePath, long FileSize)` for each file discovered. The `RelativePath` and `FileSize` SHALL be taken from the `FilePair` at the enumeration site. After enumeration completes, the system SHALL publish a `ScanCompleteEvent(long TotalFiles, long TotalBytes)` with the final counts.

#### Scenario: Binary file with matching pointer
- **WHEN** a binary file `photos/vacation.jpg` exists alongside `photos/vacation.jpg.pointer.arius`
- **THEN** the system SHALL produce a FilePair with both binary and pointer present, discovered via `File.Exists` check on the binary

#### Scenario: Binary file without pointer
- **WHEN** a binary file `documents/report.pdf` exists with no corresponding `.pointer.arius` file
- **THEN** the system SHALL produce a FilePair with binary present and pointer absent

#### Scenario: Pointer file without binary (thin archive)
- **WHEN** a pointer file `music/song.mp3.pointer.arius` exists with no corresponding binary
- **THEN** the system SHALL produce a FilePair with pointer present and binary absent, using the hash from the pointer file

#### Scenario: Pointer file with binary already emitted
- **WHEN** a pointer file `photos/vacation.jpg.pointer.arius` is encountered and `photos/vacation.jpg` exists
- **THEN** the system SHALL skip the pointer file (it was already emitted as part of the binary's FilePair)

#### Scenario: Inaccessible file
- **WHEN** a file cannot be read due to permissions or system protection
- **THEN** the system SHALL log a warning with the file path and reason, skip the file, and continue enumeration

#### Scenario: Pointer file with invalid content
- **WHEN** a `.pointer.arius` file contains content that is not a valid hex hash
- **THEN** the system SHALL log a warning and treat the file as having no valid pointer

#### Scenario: No materialization of file list
- **WHEN** enumerating a directory with 1 million files
- **THEN** the pipeline SHALL begin processing the first FilePair before enumeration completes, with no `.ToList()` or equivalent materialization

#### Scenario: Per-file scanning event published
- **WHEN** a FilePair is discovered during enumeration
- **THEN** the system SHALL publish `FileScannedEvent` with the file's `RelativePath` and `FileSize` before writing the FilePair to the channel

#### Scenario: Scan complete event published
- **WHEN** all files have been enumerated and the channel is about to be completed
- **THEN** the system SHALL publish `ScanCompleteEvent` with the total file count and total bytes

### Requirement: Progress callbacks on ArchiveOptions
`ArchiveOptions` SHALL expose two optional callback properties for injecting byte-level progress reporting:

- `Func<string, long, IProgress<long>>? CreateHashProgress` — called by the pipeline when a file begins hashing. Parameters: relative path, file size in bytes. Returns an `IProgress<long>` that receives cumulative bytes hashed. Default: `null` (no-op).
- `Func<string, long, IProgress<long>>? CreateUploadProgress` — called by the pipeline when a chunk begins uploading. Parameters: content hash, uncompressed size in bytes. Returns an `IProgress<long>` that receives cumulative bytes read from the source stream. Default: `null` (no-op).
- `Action<Func<int>>? OnHashQueueReady` — called by the pipeline when the hash input channel is created. The pipeline passes a `Func<int>` that returns `filePairChannel.Reader.Count`. Default: `null`.
- `Action<Func<int>>? OnUploadQueueReady` — called by the pipeline when the upload channels are created. The pipeline passes a `Func<int>` that returns `largeChannel.Reader.Count + sealedTarChannel.Reader.Count`. Default: `null`.

This follows the same pattern as `RestoreOptions.ConfirmRehydration` — Core exposes observable hooks, the UI injects callbacks. Core SHALL NOT take any dependency on Spectre.Console or any display library.

#### Scenario: CLI injects hash progress callback
- **WHEN** the CLI creates `ArchiveOptions`
- **THEN** it SHALL set `CreateHashProgress` to a factory that creates an `IProgress<long>` updating `TrackedFile.BytesProcessed` in `ProgressState`

#### Scenario: Core uses hash progress callback
- **WHEN** a file begins hashing and `CreateHashProgress` is not null
- **THEN** the pipeline SHALL call `CreateHashProgress(relativePath, fileSize)` and wrap the `FileStream` in a `ProgressStream` using the returned `IProgress<long>`
- **WHEN** `CreateHashProgress` is null
- **THEN** the pipeline SHALL hash the file stream directly without wrapping

#### Scenario: Core uses upload progress callback
- **WHEN** a chunk begins uploading and `CreateUploadProgress` is not null
- **THEN** the pipeline SHALL call `CreateUploadProgress(contentHash, size)` and use the returned `IProgress<long>` with `ProgressStream`
- **WHEN** `CreateUploadProgress` is null
- **THEN** the pipeline SHALL use a no-op `IProgress<long>` (current behavior)

#### Scenario: Pipeline registers hash queue depth
- **WHEN** the pipeline creates `filePairChannel` and `OnHashQueueReady` is not null
- **THEN** the pipeline SHALL call `OnHashQueueReady(() => filePairChannel.Reader.Count)`

#### Scenario: Pipeline registers upload queue depth
- **WHEN** the pipeline creates `largeChannel` and `sealedTarChannel` and `OnUploadQueueReady` is not null
- **THEN** the pipeline SHALL call `OnUploadQueueReady(() => largeChannel.Reader.Count + sealedTarChannel.Reader.Count)`

### Requirement: Tar builder
The system SHALL bundle small files into tar archives using a single tar builder. Files inside the tar SHALL be named by their content-hash (not original path). The tar builder SHALL seal and hand off the tar to the upload channel when the accumulated uncompressed size reaches `--tar-target-size` (default 64 MB). After sealing, the builder SHALL immediately start a new tar. The tar builder SHALL stream to a temp file on disk (not memory). Depth-first enumeration provides natural directory affinity. The tar hash SHALL be computed using `_encryption.ComputeHashAsync(fs)` (passphrase-seeded when a passphrase is provided) for consistency with content hash computation.

The tar builder SHALL publish `TarBundleStartedEvent()` when initializing a new tar (before writing the first entry).

#### Scenario: Tar sealing at target size
- **WHEN** accumulated small files in the current tar reach 64 MB uncompressed
- **THEN** the system SHALL seal the tar, compute its tar-hash via `_encryption.ComputeHashAsync`, and hand it off for upload

#### Scenario: Partial tar at end of archive
- **WHEN** the archive run completes with a partially filled tar (< 64 MB)
- **THEN** the system SHALL seal and upload the partial tar

#### Scenario: Tar with single file
- **WHEN** only one small file needs archiving
- **THEN** the system SHALL create a tar with that single file entry

#### Scenario: Tar upload format
- **WHEN** a sealed tar is uploaded
- **THEN** the blob SHALL be stored at `chunks/<tar-hash>` with `arius-type: tar` and content type `application/aes256cbc+tar+gzip` (or `application/tar+gzip` without passphrase)

#### Scenario: Tar hash uses passphrase-seeded hash
- **WHEN** a tar is sealed with a passphrase configured
- **THEN** the tar-hash SHALL be `SHA256(passphrase + tarBytes)` via `_encryption.ComputeHashAsync`

#### Scenario: TarBundleStartedEvent published on new tar
- **WHEN** the tar builder initializes a new tar archive (before writing the first entry)
- **THEN** the system SHALL publish `TarBundleStartedEvent()` with no parameters

#### Scenario: TarBundleStartedEvent on first tar
- **WHEN** the first small file arrives at the tar builder
- **THEN** `TarBundleStartedEvent()` SHALL be published before the first `TarEntryAddedEvent`

#### Scenario: TarBundleStartedEvent on subsequent tars
- **WHEN** a tar is sealed and the next small file arrives
- **THEN** a new `TarBundleStartedEvent()` SHALL be published before the new tar's first `TarEntryAddedEvent`

### Requirement: Large file upload
The system SHALL upload large files individually as chunks using streaming upload. The upload pipeline SHALL use the streaming chain: `ProgressStream(FileStream) -> GZipStream -> EncryptingStream -> CountingStream -> OpenWriteAsync` to stream data to `chunks/<content-hash>`. The blob metadata SHALL include `arius-type: large`, `original-size`, and `chunk-size` (from `CountingStream.BytesWritten`), written via `SetMetadataAsync` after the upload stream closes. Content type SHALL be set to `application/aes256cbc+gzip` (encrypted) or `application/gzip` (plaintext). Multiple large files SHALL upload concurrently using `Parallel.ForEachAsync`.

#### Scenario: Large file upload with encryption
- **WHEN** a 50 MB file is uploaded with a passphrase
- **THEN** the system SHALL stream the file through ProgressStream -> GZipStream -> EncryptingStream -> CountingStream -> OpenWriteAsync to `chunks/<content-hash>` with content type `application/aes256cbc+gzip`

#### Scenario: Large file upload without encryption
- **WHEN** a 50 MB file is uploaded without a passphrase
- **THEN** the system SHALL stream the file through the chain with content type `application/gzip`

#### Scenario: Metadata written after upload
- **WHEN** a chunk upload finishes
- **THEN** the blob metadata SHALL have `arius-type: large`, `original-size`, and `chunk-size` set via `SetMetadataAsync`

#### Scenario: Concurrent uploads
- **WHEN** 10 large files need uploading with 4 upload workers
- **THEN** the system SHALL process them via `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 4`

## ADDED Requirements

### Requirement: FileScannedEvent per-file notification
The `FileScannedEvent` record SHALL be defined as `FileScannedEvent(string RelativePath, long FileSize) : INotification`. This replaces the previous `FileScannedEvent(long TotalFiles)` batch event. The event SHALL be published once per file discovered during enumeration, providing the file's relative path and size in bytes.

#### Scenario: Per-file event during enumeration
- **WHEN** a file `photos/vacation.jpg` (1.2 MB) is discovered during enumeration
- **THEN** the system SHALL publish `FileScannedEvent("photos/vacation.jpg", 1200000)`

#### Scenario: Events published before channel write
- **WHEN** a FilePair is discovered
- **THEN** `FileScannedEvent` SHALL be published before the FilePair is written to `filePairChannel`

### Requirement: ScanCompleteEvent notification
A new notification record `ScanCompleteEvent(long TotalFiles, long TotalBytes) : INotification` SHALL be published once when file enumeration completes. It SHALL carry the final total file count and total byte count. A corresponding `ILogger` debug-level log line SHALL be emitted.

#### Scenario: Scan complete after enumeration
- **WHEN** enumeration finishes having discovered 1523 files totaling 5 GB
- **THEN** the system SHALL publish `ScanCompleteEvent(1523, 5000000000)`

#### Scenario: ScanCompleteEvent published before channel completion
- **WHEN** all files have been enumerated
- **THEN** `ScanCompleteEvent` SHALL be published before `filePairChannel.Writer.Complete()` is called

### Requirement: TarBundleStartedEvent notification
A new notification record `TarBundleStartedEvent() : INotification` SHALL be published by the tar builder when it initializes a new tar archive. The event SHALL have no parameters — bundle numbering is a CLI display concern, not a Core concern. A corresponding `ILogger` debug-level log line SHALL be emitted.

#### Scenario: Event published before first entry
- **WHEN** the tar builder creates a new tar archive
- **THEN** `TarBundleStartedEvent()` SHALL be published before the first `TarEntryAddedEvent` for that tar

#### Scenario: Event published on each new tar
- **WHEN** a tar is sealed at 64 MB and a new tar begins
- **THEN** a new `TarBundleStartedEvent()` SHALL be published for the new tar

### Requirement: TAR upload ProgressStream wiring
The tar upload stage SHALL wrap the sealed tar's `FileStream` in a `ProgressStream` when `CreateUploadProgress` is provided. The `ProgressStream` SHALL report cumulative bytes read to the `IProgress<long>` returned by `CreateUploadProgress(tarHash, uncompressedSize)`. This enables byte-level upload progress for TAR bundles in the display.

#### Scenario: TAR upload with progress
- **WHEN** a sealed tar with hash `"tarhash1"` and uncompressed size 52 MB is uploaded and `CreateUploadProgress` is not null
- **THEN** the pipeline SHALL call `CreateUploadProgress("tarhash1", 52MB)` and wrap the tar `FileStream` in `ProgressStream` using the returned `IProgress<long>`

#### Scenario: TAR upload without progress callback
- **WHEN** a sealed tar is uploaded and `CreateUploadProgress` is null
- **THEN** the pipeline SHALL upload using a no-op `IProgress<long>` (no ProgressStream overhead)

#### Scenario: Progress bytes match source stream
- **WHEN** the tar upload streams through `ProgressStream -> GZipStream -> EncryptingStream -> OpenWriteAsync`
- **THEN** the `IProgress<long>` SHALL receive cumulative bytes of the uncompressed tar data read from the source `FileStream`
