## ADDED Requirements

### Requirement: Parallel chunk downloads
The restore pipeline SHALL download chunks using `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 4`, replacing the sequential `foreach` loop. Each chunk download (large file or tar bundle) SHALL execute independently and concurrently. The `filesRestored` counter SHALL use `Interlocked.Increment` for thread-safe updates from parallel workers.

#### Scenario: Four concurrent downloads
- **WHEN** 20 chunks are available for download
- **THEN** the pipeline SHALL process up to 4 chunks concurrently via `Parallel.ForEachAsync`
- **AND** all 20 chunks SHALL be downloaded and restored

#### Scenario: Thread-safe counter updates
- **WHEN** two parallel workers each restore a file simultaneously
- **THEN** `filesRestored` SHALL be incremented atomically via `Interlocked.Increment`
- **AND** the final count SHALL equal the total number of files restored

#### Scenario: Parallel mediator publish
- **WHEN** parallel workers publish `FileRestoredEvent` concurrently
- **THEN** all events SHALL be delivered to handlers without data races

#### Scenario: Error in one worker does not corrupt others
- **WHEN** a download fails in one parallel worker
- **THEN** the exception SHALL propagate to `Parallel.ForEachAsync` and cancel remaining workers
- **AND** no partial state SHALL remain in `ProgressState`

### Requirement: Download progress callback on RestoreOptions
`RestoreOptions` SHALL expose a `CreateDownloadProgress` property of type `Func<string, long, DownloadKind, IProgress<long>>?` (defaulting to `null`). The factory accepts: identifier (string), compressed size (long), and kind (`DownloadKind` enum). `DownloadKind` SHALL have values `LargeFile` and `TarBundle`.

For large files, the identifier SHALL be the file's `RelativePath`. For tar bundles, the identifier SHALL be the chunk hash.

When `CreateDownloadProgress` is not null, the pipeline SHALL call it before each download and wrap the download stream with `ProgressStream` using the returned `IProgress<long>`.

#### Scenario: Large file download with progress
- **WHEN** `CreateDownloadProgress` is set and a large file `photos/sunset.jpg` (25.4 MB compressed) is downloaded
- **THEN** the pipeline SHALL call `CreateDownloadProgress("photos/sunset.jpg", 25_400_000, DownloadKind.LargeFile)`
- **AND** wrap the download stream with `ProgressStream` reporting cumulative bytes to the returned `IProgress<long>`

#### Scenario: Tar bundle download with progress
- **WHEN** `CreateDownloadProgress` is set and a tar bundle (chunk hash `ab12cd34`, 15.2 MB compressed, containing 3 files totaling 847 KB original) is downloaded
- **THEN** the pipeline SHALL call `CreateDownloadProgress("ab12cd34", 15_200_000, DownloadKind.TarBundle)`
- **AND** wrap the download stream with `ProgressStream`

#### Scenario: CreateDownloadProgress is null
- **WHEN** `CreateDownloadProgress` is null (default)
- **THEN** the pipeline SHALL download without wrapping in `ProgressStream`
- **AND** behavior SHALL be identical to the current implementation (no progress callbacks)

### Requirement: Tree traversal progress events
During `WalkTreeAsync`, the pipeline SHALL emit `TreeTraversalProgressEvent(int FilesFound)` periodically as file entries are discovered. The event SHALL be emitted in batches (not per-file) to minimize overhead -- at least every 10 files or every 100ms, whichever comes first.

#### Scenario: Progress during traversal of large tree
- **WHEN** tree traversal discovers 1,247 files across 200 tree blobs
- **THEN** the pipeline SHALL emit multiple `TreeTraversalProgressEvent` notifications with increasing `FilesFound` counts
- **AND** the final `TreeTraversalCompleteEvent` SHALL follow with `FileCount: 1247`

#### Scenario: Small tree emits at least one progress event
- **WHEN** tree traversal discovers 5 files in a single tree blob
- **THEN** the pipeline SHALL emit at least one `TreeTraversalProgressEvent(5)` before `TreeTraversalCompleteEvent`

### Requirement: Chunk download completed event for tar bundles
The pipeline SHALL emit a `ChunkDownloadCompletedEvent(string ChunkHash, int FilesRestored, long CompressedSize)` after a tar bundle has been fully downloaded and extracted. This event is needed to remove the `TrackedDownload` entry for tar bundles (large files are removed via `FileRestoredEvent` since they map 1:1 to chunks).

#### Scenario: Tar bundle completion event
- **WHEN** a tar bundle containing 3 files finishes downloading and extracting
- **THEN** the pipeline SHALL emit `ChunkDownloadCompletedEvent(chunkHash, 3, compressedSize)`

## MODIFIED Requirements

### Requirement: Streaming restore (no local cache)
The system SHALL restore files by streaming chunks directly to their final path without intermediate local caching. For large files: download → decrypt → gunzip → write to final path. For tar bundles: download → decrypt → gunzip → iterate tar entries → extract needed files by content-hash name → write to final paths. Peak temp disk usage SHALL be zero (fully streaming). All files needing a given chunk SHALL be grouped and extracted in a single streaming pass.

Downloads SHALL execute in parallel with up to 4 concurrent workers. When `RestoreOptions.CreateDownloadProgress` is provided, the download stream SHALL be wrapped with `ProgressStream` to report byte-level progress via `IProgress<long>`.

#### Scenario: Large file streaming restore
- **WHEN** restoring a 2 GB large file
- **THEN** the system SHALL stream download → decrypt → gunzip → write directly to the target path with no intermediate temp file

#### Scenario: Large file streaming restore with progress
- **WHEN** restoring a 2 GB large file with `CreateDownloadProgress` set
- **THEN** the system SHALL wrap the download stream with `ProgressStream`
- **AND** the `IProgress<long>` callback SHALL receive cumulative byte counts as data is read

#### Scenario: Tar bundle streaming extract
- **WHEN** restoring 3 files that are bundled in the same tar
- **THEN** the system SHALL download the tar once, stream through the tar entries, and extract the 3 matching content-hash entries to their final paths

#### Scenario: Tar entry not needed
- **WHEN** a tar contains 300 files but only 2 are needed for this restore
- **THEN** the system SHALL skip the 298 unneeded entries during streaming tar iteration

#### Scenario: Parallel downloads saturate bandwidth
- **WHEN** 4 chunks are being downloaded concurrently
- **THEN** each SHALL independently stream download → decrypt → gunzip → write without contention

### Requirement: Idempotent restore
Restore SHALL be fully idempotent. Re-running the same restore command SHALL: skip files already restored correctly (hash match), download newly rehydrated chunks, skip still-pending rehydrations (must not issue duplicate requests), and report remaining files. Each run is a self-contained scan-and-act cycle with no persistent local state. The restore pipeline SHALL publish notification events throughout:

- `RestoreStartedEvent(TotalFiles)` before beginning downloads
- `FileRestoredEvent(RelativePath, FileSize)` after each file is written to disk
- `FileSkippedEvent(RelativePath, FileSize)` for each file skipped due to hash match
- `RehydrationStartedEvent(ChunkCount, TotalBytes)` when rehydration is kicked off
- `TreeTraversalProgressEvent(FilesFound)` periodically during tree traversal
- `ChunkDownloadCompletedEvent(ChunkHash, FilesRestored, CompressedSize)` after each tar bundle download completes

`FileRestoredEvent` and `FileSkippedEvent` SHALL carry `long FileSize` (the file's uncompressed size in bytes) so the CLI can accumulate bytes-restored/skipped and show per-file sizes in the restore tail display.

`ChunkResolutionCompleteEvent` SHALL carry `TotalOriginalBytes` and `TotalCompressedBytes` in addition to the existing `ChunkGroups`, `LargeCount`, and `TarCount` fields. These byte totals SHALL be computed by summing `OriginalSize` and `CompressedSize` from the chunk index entries for all chunks to be downloaded.

#### Scenario: Partial restore re-run
- **WHEN** a restore previously restored 500 of 1000 files and rehydration has completed for 300 more chunks
- **THEN** re-running SHALL skip the 500 completed files, restore the 300 newly available, and report 200 still pending

#### Scenario: Full restore complete
- **WHEN** all files have been restored across multiple runs
- **THEN** the system SHALL report all files restored and prompt to clean up `chunks-rehydrated/`

#### Scenario: Progress events emitted during restore
- **WHEN** a restore operation begins
- **THEN** the system SHALL publish `RestoreStartedEvent(TotalFiles)` before downloading, and `FileRestoredEvent(path, size)` / `FileSkippedEvent(path, size)` for each file processed

#### Scenario: ChunkResolutionCompleteEvent carries byte totals
- **WHEN** chunk resolution completes with 10 chunks totaling 500 MB original, 200 MB compressed
- **THEN** `ChunkResolutionCompleteEvent` SHALL carry `TotalOriginalBytes: 500_000_000` and `TotalCompressedBytes: 200_000_000`
