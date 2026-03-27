## REVISED Requirements (v2 — TarBundleSealingEvent enrichment)

### Requirement: Progress callbacks on ArchiveOptions
`ArchiveOptions` SHALL expose two optional callback properties for injecting byte-level progress reporting:

- `Func<string, long, IProgress<long>>? CreateHashProgress` — called by the pipeline when a file begins hashing. Parameters: relative path, file size in bytes. Returns an `IProgress<long>` that receives cumulative bytes hashed. Default: `null` (no-op).
- `Func<string, long, IProgress<long>>? CreateUploadProgress` — called by the pipeline when a chunk begins uploading. Parameters: content hash, uncompressed size in bytes. Returns an `IProgress<long>` that receives cumulative bytes read from the source stream. Default: `null` (no-op).

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

### Requirement: ProgressStream wiring for hash path
The archive pipeline SHALL wrap the file `FileStream` in a `ProgressStream` during hash computation when `CreateHashProgress` is provided. The `ProgressStream` SHALL report cumulative source bytes read to the `IProgress<long>` returned by the factory. The hash computation (`ComputeHashAsync`) SHALL read from the `ProgressStream` instead of the raw `FileStream`.

#### Scenario: Large file hashing with progress
- **WHEN** a 5 GB file is hashed with `CreateHashProgress` set
- **THEN** the `IProgress<long>` callback SHALL receive incremental byte counts as the hash function reads through the stream
- **AND** the hash result SHALL be identical to hashing the raw stream (ProgressStream is transparent)

#### Scenario: Hashing without progress callback
- **WHEN** a file is hashed with `CreateHashProgress` set to null
- **THEN** the pipeline SHALL hash the raw `FileStream` directly (no ProgressStream overhead)

### Requirement: TarEntryAddedEvent
A notification record `TarEntryAddedEvent(string ContentHash, int CurrentEntryCount, long CurrentTarSize)` SHALL be published after each file is written to the tar archive (after `tarWriter.WriteEntryAsync`). A corresponding `ILogger` debug-level log line SHALL be emitted for consistency with existing event logging patterns. (Already implemented, unchanged.)

#### Scenario: File added to tar
- **WHEN** a small file is added to the current tar bundle
- **THEN** the pipeline SHALL publish `TarEntryAddedEvent` with the file's content hash, the updated entry count, and the updated cumulative uncompressed size

### Requirement: TarBundleSealingEvent with content hashes
The `TarBundleSealingEvent` record SHALL be enriched to include the list of content hashes in the sealed tar bundle:

`TarBundleSealingEvent(int EntryCount, long UncompressedSize, string TarHash, IReadOnlyList<string> ContentHashes)`

The `TarHash` parameter is the tar bundle's content hash (the bundle-level digest). The `ContentHashes` parameter SHALL contain the content hash of every file entry in the tar, in the order they were added (projected from the existing `tarEntries` list in `ArchivePipelineHandler`).

#### Scenario: Tar sealed with 5 files
- **WHEN** a tar bundle containing 5 files is sealed
- **THEN** the pipeline SHALL publish `TarBundleSealingEvent(5, totalSize, ["hash1", "hash2", "hash3", "hash4", "hash5"])` where each hash corresponds to a file in the tar

#### Scenario: CLI uses content hashes for state transition
- **WHEN** the CLI receives `TarBundleSealingEvent` with `ContentHashes`
- **THEN** it SHALL use the `ContentHash → RelativePath` reverse map to find each file's `TrackedFile` entry and transition its state from `QueuedInTar` to `UploadingTar`

### Requirement: Streaming hash computation (enriched)
The system SHALL compute content hashes by streaming file data through the hash function without loading the entire file into memory. During hashing, the system SHALL publish `FileHashingEvent` with the file's relative path and file size (in bytes) to enable per-file progress display. When `ArchiveOptions.CreateHashProgress` is provided, the file stream SHALL be wrapped in `ProgressStream` before being passed to `ComputeHashAsync`. (Unchanged from previous design.)

#### Scenario: FileHashingEvent emitted with size
- **WHEN** a file begins hashing
- **THEN** the system SHALL publish `FileHashingEvent` with `RelativePath` and `FileSize`
