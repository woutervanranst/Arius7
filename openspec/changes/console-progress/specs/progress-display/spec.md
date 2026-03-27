## REVISED Requirements

### Requirement: Mediator handler discovery
The CLI assembly SHALL have `Mediator.SourceGenerator` and `Mediator.Abstractions` as direct package references so the source generator discovers `INotificationHandler<T>` implementations in `Arius.Cli`. All 12 archive and restore notification handlers SHALL be invoked when Core publishes events.

#### Scenario: Handler invoked on publish
- **WHEN** Core publishes `FileScannedEvent` via `_mediator.Publish()`
- **THEN** the `FileScannedHandler` in `Arius.Cli` SHALL be invoked and update `ProgressState`

#### Scenario: Production DI wiring
- **WHEN** `BuildProductionServices` creates the service provider
- **THEN** all 12 notification handlers SHALL be resolvable by the source-generated Mediator

### Requirement: Concurrent per-file progress state
The `ProgressState` class SHALL track multiple in-flight operations concurrently using `ConcurrentDictionary<string, FileProgress>` for both hashing and uploading. `FileProgress` SHALL contain: `FileName` (string), `TotalBytes` (long), and `BytesProcessed` (long, updated via `Interlocked`). Handlers SHALL add entries on operation start and remove them on operation completion.

#### Scenario: Four concurrent hash operations
- **WHEN** 4 files are being hashed in parallel by 4 hash workers
- **THEN** `ProgressState.InFlightHashes` SHALL contain 4 entries, each independently tracking file name, total bytes, and bytes processed

#### Scenario: Hash operation lifecycle
- **WHEN** `FileHashingEvent("video.mp4", 5GB)` is published
- **THEN** the handler SHALL add an entry to `InFlightHashes` keyed by `"video.mp4"`
- **WHEN** `FileHashedEvent("video.mp4", hash)` is published
- **THEN** the handler SHALL remove the entry from `InFlightHashes`

#### Scenario: Upload operation lifecycle
- **WHEN** `ChunkUploadingEvent("a1b2c3d4", 1GB)` is published
- **THEN** the handler SHALL add an entry to `InFlightUploads` keyed by `"a1b2c3d4"`
- **WHEN** `ChunkUploadedEvent("a1b2c3d4", 800MB)` is published
- **THEN** the handler SHALL remove the entry from `InFlightUploads`

#### Scenario: Byte-level progress update
- **WHEN** the `IProgress<long>` callback for a hashing file reports 2.5 GB read
- **THEN** `InFlightHashes["video.mp4"].BytesProcessed` SHALL be updated to 2,500,000,000 via `Interlocked.Exchange`

#### Scenario: Thread safety under contention
- **WHEN** 4 hash workers and 4 upload workers concurrently add, update, and remove entries
- **THEN** no data races SHALL occur and aggregate counters (`FilesHashed`, `ChunksUploaded`, etc.) SHALL remain correct

### Requirement: Tar bundling progress
The `ProgressState` class SHALL track the current tar bundle's entry count and accumulated size. A `TarEntryAddedHandler` SHALL handle `TarEntryAddedEvent` and update these fields.

#### Scenario: Tar filling up
- **WHEN** 7 small files have been added to the current tar bundle
- **THEN** `ProgressState.CurrentTarEntryCount` SHALL be 7 and `CurrentTarSize` SHALL reflect the accumulated uncompressed size

#### Scenario: Tar sealed and new tar started
- **WHEN** `TarBundleSealingEvent` is published
- **THEN** the handler SHALL reset `CurrentTarEntryCount` and `CurrentTarSize` to 0

### Requirement: Archive notification handlers
The system SHALL implement `INotificationHandler<T>` for all 8 archive notification events plus `TarEntryAddedEvent` (9 total). Each handler SHALL update the corresponding field(s) on `ProgressState`. Handlers SHALL be thin (counter increment / dictionary update only, no business logic).

#### Scenario: FileScannedEvent sets total
- **WHEN** `FileScannedEvent(TotalFiles: 1523)` is published
- **THEN** the handler SHALL set `ProgressState.TotalFiles = 1523`

#### Scenario: FileHashedEvent increments counter and removes in-flight entry
- **WHEN** `FileHashedEvent("video.mp4", hash)` is published
- **THEN** the handler SHALL increment `ProgressState.FilesHashed` and remove `"video.mp4"` from `InFlightHashes`

#### Scenario: ChunkUploadedEvent increments counters and removes in-flight entry
- **WHEN** `ChunkUploadedEvent("a1b2c3d4", 800MB)` is published
- **THEN** the handler SHALL increment `ChunksUploaded`, add `CompressedSize` to `BytesUploaded`, and remove `"a1b2c3d4"` from `InFlightUploads`

### Requirement: Restore notification handlers
The system SHALL implement `INotificationHandler<T>` for all 4 restore notification events. Each handler SHALL update the corresponding field on `ProgressState`.

#### Scenario: RestoreStartedEvent sets total
- **WHEN** `RestoreStartedEvent(TotalFiles: 1000)` is published
- **THEN** the handler SHALL set `ProgressState.RestoreTotalFiles = 1000`

#### Scenario: FileRestoredEvent increments counter
- **WHEN** `FileRestoredEvent` is published
- **THEN** the handler SHALL increment `ProgressState.FilesRestored`

#### Scenario: FileSkippedEvent increments counter
- **WHEN** `FileSkippedEvent` is published
- **THEN** the handler SHALL increment `ProgressState.FilesSkipped`

### Requirement: Multi-stage archive progress display with per-file visibility
The CLI SHALL display a Spectre.Console `Progress` with four progress tasks during archive: Scanning, Hashing, Bundling, and Uploading. The Scanning task SHALL start indeterminate and transition to determinate when `TotalFiles` is known. The Hashing task SHALL show `FilesHashed / TotalFiles`. The Bundling task SHALL show the current tar entry count. The Uploading task SHALL start indeterminate and transition to determinate when total chunk count becomes known (after dedup completes).

Below the Hashing and Uploading aggregate bars, the display SHALL dynamically add and remove `ProgressTask` entries for each in-flight operation, showing file name, byte-level percentage, and bytes processed vs total. Up to 4 hash sub-lines and 4 upload sub-lines SHALL be shown (matching worker counts).

#### Scenario: Full archive display
- **WHEN** 789 of 1523 files are hashed, 4 files are hashing, 7 files in current tar, 3 of 11 chunks uploaded with 2 uploads in-flight
- **THEN** the display SHALL show:
  ```
  [Scanning ]  ████████████████████████  100%   1523 files
  [Hashing  ]  ████████████░░░░░░░░░░░   52%   789/1523
    video.mp4      ██████████░░░░░░░  62%  3.1 GB / 5.0 GB
    backup.tar     ████░░░░░░░░░░░░░  28%  560 MB / 2.0 GB
    data.db        ██████████████░░░  89%  890 MB / 1.0 GB
    photo.raw      ██░░░░░░░░░░░░░░░  12%   60 MB / 500 MB
  [Bundling ]  ░░░░░░░░░░░░░░░░░░░░░░░        7 files in current tar
  [Uploading]  ██████░░░░░░░░░░░░░░░░░  27%   3/11 chunks
    a1b2c3d4..     ███████░░░░░░░░░░  45%  450 MB / 1.0 GB
    e5f6a7b8..     █░░░░░░░░░░░░░░░░   8%   80 MB / 1.0 GB
  ```

#### Scenario: Dynamic sub-line lifecycle
- **WHEN** a file starts hashing
- **THEN** a new `ProgressTask` sub-line SHALL appear below the Hashing bar
- **WHEN** the file finishes hashing
- **THEN** the sub-line SHALL be removed

#### Scenario: Non-interactive terminal
- **WHEN** the terminal does not support interactive output (piped or CI)
- **THEN** the CLI SHALL fall back to static summary output (no progress bars)

### Requirement: Restore progress with TCS phase coordination
The CLI SHALL use `TaskCompletionSource` pairs to coordinate between the restore pipeline's callback invocations and the console display. The restore flow SHALL have distinct phases:

- **Plan phase** (steps 1-6): No live progress display. The pipeline runs until `ConfirmRehydration` fires.
- **Cost confirmation**: The callback signals via TCS, the CLI renders cost tables and prompts on a clean console, then unblocks the pipeline via a response TCS.
- **Download phase** (step 7+): `AnsiConsole.Progress()` with a determinate bar for files restored/total.
- **Cleanup confirmation**: Progress display completes (AutoClear), `ConfirmCleanup` renders on a clean console.

#### Scenario: Cost tables render cleanly
- **WHEN** the restore pipeline invokes `ConfirmRehydration`
- **THEN** the cost summary table, cost breakdown table, and selection prompt SHALL render without interference from any live progress display

#### Scenario: Download progress after confirmation
- **WHEN** the user selects a rehydration priority
- **THEN** a Spectre.Console `Progress` SHALL start showing files restored out of total

#### Scenario: Cleanup prompt renders cleanly
- **WHEN** `ConfirmCleanup` is invoked after all downloads complete
- **THEN** the progress display SHALL have been cleared and the confirm prompt SHALL render without interference

#### Scenario: Pipeline completes without rehydration needed
- **WHEN** all chunks are available (no rehydration needed) and `ConfirmRehydration` is not invoked
- **THEN** the CLI SHALL show a progress bar for the download phase directly

### Requirement: Responsive poll loop
The archive and restore display poll loops SHALL use `Task.WhenAny(pipelineTask, Task.Delay(100))` instead of unconditional `Task.Delay(100)` to respond immediately when the pipeline completes while still throttling refresh rate during active operation.

#### Scenario: Pipeline finishes mid-delay
- **WHEN** the pipeline completes 10ms into a 100ms delay cycle
- **THEN** the display SHALL update immediately rather than waiting the remaining 90ms
