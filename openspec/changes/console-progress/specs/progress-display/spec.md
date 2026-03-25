## ADDED Requirements

### Requirement: Shared progress state model
The system SHALL provide a `ProgressState` class registered as a singleton in DI that tracks all progress counters for archive and restore operations. Counters SHALL be updated using `Interlocked` operations for thread safety. The state SHALL include: `FilesScanned` (long), `TotalFiles` (long?, null until enumeration completes), `FilesHashing` (int, in-flight count), `FilesHashed` (long), `ChunksUploading` (int, in-flight count), `ChunksUploaded` (long), `BytesUploaded` (long), `TarsBundled` (int), `TarsUploaded` (int), and per-file progress fields for the currently active large file hash and upload.

#### Scenario: Thread-safe counter updates
- **WHEN** multiple notification handlers update `ProgressState` concurrently from different pipeline stages
- **THEN** all counter updates SHALL be atomic (using `Interlocked`) and no data races SHALL occur

#### Scenario: Singleton lifetime
- **WHEN** the DI container resolves `ProgressState`
- **THEN** the same instance SHALL be returned to all handlers and the display component

### Requirement: Archive notification handlers
The system SHALL implement `INotificationHandler<T>` for all 8 archive notification events: `FileScannedEvent`, `FileHashingEvent`, `FileHashedEvent`, `ChunkUploadingEvent`, `ChunkUploadedEvent`, `TarBundleSealingEvent`, `TarBundleUploadedEvent`, `SnapshotCreatedEvent`. Each handler SHALL update the corresponding field on `ProgressState`. Handlers SHALL be thin (counter increment only, no business logic) to avoid adding latency to the pipeline.

#### Scenario: FileScannedEvent sets total
- **WHEN** `FileScannedEvent(TotalFiles: 1523)` is published
- **THEN** the handler SHALL set `ProgressState.TotalFiles = 1523`

#### Scenario: FileHashedEvent increments counter
- **WHEN** `FileHashedEvent` is published
- **THEN** the handler SHALL increment `ProgressState.FilesHashed`

#### Scenario: ChunkUploadedEvent increments counters
- **WHEN** `ChunkUploadedEvent(ContentHash, CompressedSize: 5000000)` is published
- **THEN** the handler SHALL increment `ProgressState.ChunksUploaded` and add `CompressedSize` to `ProgressState.BytesUploaded`

#### Scenario: Handler latency
- **WHEN** any notification handler executes
- **THEN** it SHALL complete in microseconds (Interlocked operation only, no I/O or allocation)

### Requirement: Restore notification handlers
The system SHALL implement `INotificationHandler<T>` for all 4 restore notification events: `RestoreStartedEvent`, `FileRestoredEvent`, `FileSkippedEvent`, `RehydrationStartedEvent`. Each handler SHALL update the corresponding field on `ProgressState`.

#### Scenario: RestoreStartedEvent sets total
- **WHEN** `RestoreStartedEvent(TotalFiles: 1000)` is published
- **THEN** the handler SHALL set `ProgressState.RestoreTotalFiles = 1000`

#### Scenario: FileRestoredEvent increments counter
- **WHEN** `FileRestoredEvent` is published
- **THEN** the handler SHALL increment `ProgressState.FilesRestored`

#### Scenario: FileSkippedEvent increments counter
- **WHEN** `FileSkippedEvent` is published
- **THEN** the handler SHALL increment `ProgressState.FilesSkipped`

### Requirement: Multi-stage archive progress display
The CLI SHALL display a Spectre.Console `Progress` with three concurrent progress tasks during archive: Scanning, Hashing, and Uploading. The Scanning task SHALL start as indeterminate and transition to determinate when `ProgressState.TotalFiles` becomes non-null. The Hashing task SHALL show `FilesHashed / TotalFiles`. The Uploading task SHALL show `ChunksUploaded` count and `BytesUploaded`. The display SHALL refresh at a rate sufficient for smooth visual updates (Spectre.Console default ~10 Hz).

#### Scenario: Indeterminate scanning
- **WHEN** archive starts and enumeration is in progress
- **THEN** the Scanning bar SHALL display as indeterminate with a counter of files found so far

#### Scenario: Scanning transitions to determinate
- **WHEN** `ProgressState.TotalFiles` transitions from null to 1523
- **THEN** the Scanning bar SHALL switch to determinate showing 100% (1523/1523)

#### Scenario: Concurrent stage progress
- **WHEN** 789 of 1523 files are hashed and 45 chunks are uploaded
- **THEN** the display SHALL show Scanning 100%, Hashing 52% (789/1523), Uploading (45 chunks)

### Requirement: Per-file progress for large files
The CLI SHALL display per-file byte-level progress for in-flight large file operations (hashing and uploading) below the aggregate progress bars. The display SHALL show the file name, percentage, and bytes processed vs total. The `IProgress<long>` callbacks from `ProgressStream` SHALL update `ProgressState` with the current file name and bytes read.

#### Scenario: Large file hash progress
- **WHEN** a 4.7 GB file is being hashed and 2.1 GB have been read
- **THEN** the display SHALL show a sub-line: `Hashing: large-video.mp4  45% (2.1 GB / 4.7 GB)`

#### Scenario: Large file upload progress
- **WHEN** a 2.3 GB file is being uploaded and 1.8 GB of source bytes have been read through ProgressStream
- **THEN** the display SHALL show a sub-line: `Uploading: backup-image.iso  78% (1.8 GB / 2.3 GB)`

#### Scenario: Multiple concurrent uploads
- **WHEN** 3 large files are uploading concurrently
- **THEN** the display SHALL show one sub-line per in-flight upload

### Requirement: Restore progress display
The CLI SHALL display a Spectre.Console `Progress` during the restore download phase with a determinate progress task showing files restored out of total. On exit, the CLI SHALL print a summary line: "N files restored, M files skipped, P files pending rehydration."

#### Scenario: Restore progress bar
- **WHEN** 500 of 1000 files have been restored
- **THEN** the display SHALL show a progress bar at 50% (500/1000)

#### Scenario: Restore exit summary
- **WHEN** restore completes with 500 restored, 200 skipped, and 300 pending rehydration
- **THEN** the CLI SHALL print "500 files restored, 200 files skipped, 300 files pending rehydration"
