## REVISED Requirements

### Requirement: Archive progress display with dynamic per-file sub-lines
The CLI SHALL display a Spectre.Console `Progress` (with `AutoRefresh(true)`) during archive with four aggregate progress tasks: Scanning, Hashing, Bundling, and Uploading. The display loop SHALL use `Task.WhenAny(pipelineTask, Task.Delay(100))` for responsive updates.

The Scanning task SHALL start indeterminate and transition to determinate when `ProgressState.TotalFiles` becomes known. The Hashing task SHALL show `FilesHashed / TotalFiles`. The Bundling task SHALL show the current tar entry count (indeterminate, description-only — resets on each seal). The Uploading task SHALL start indeterminate and transition to determinate when `ProgressState.TotalChunks` becomes known (after dedup completes).

Below the Hashing and Uploading aggregate bars, the display loop SHALL dynamically add and remove `ProgressTask` entries for each in-flight operation by reading `ProgressState.InFlightHashes` and `ProgressState.InFlightUploads` (`ConcurrentDictionary<string, FileProgress>`). Each sub-line SHALL show the file/chunk name, byte-level percentage, and bytes processed vs total. Sub-lines SHALL be added when a new key appears and removed when the key is removed from the dictionary.

The CLI SHALL inject `IProgress<long>` callbacks into Core via `ArchiveOptions.CreateHashProgress` and `ArchiveOptions.CreateUploadProgress`. These factory callbacks SHALL create progress reporters that update `FileProgress.BytesProcessed` (via `Interlocked.Exchange`) in the corresponding `ConcurrentDictionary` entry.

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
- **WHEN** a file starts hashing and appears in `InFlightHashes`
- **THEN** the display loop SHALL add a new `ProgressTask` sub-line below the Hashing bar
- **WHEN** the file finishes hashing and is removed from `InFlightHashes`
- **THEN** the display loop SHALL remove the sub-line

#### Scenario: Upload transitions from indeterminate to determinate
- **WHEN** dedup completes and `ProgressState.TotalChunks` is set
- **THEN** the Uploading task SHALL transition from indeterminate to determinate with `MaxValue = TotalChunks`

#### Scenario: Non-interactive terminal
- **WHEN** the terminal does not support interactive output (piped or CI)
- **THEN** the CLI SHALL fall back to static summary output (no progress bars)

### Requirement: Restore progress display with TCS phase coordination
The CLI SHALL use `TaskCompletionSource` pairs to coordinate between the restore pipeline's callback invocations and the console display, avoiding concurrent Spectre Console rendering (which is not thread-safe). The restore flow SHALL have distinct phases:

1. **Plan phase** (pipeline steps 1-6): No live progress display. The pipeline runs until it invokes `ConfirmRehydration` or completes without needing rehydration.
2. **Cost confirmation**: The `ConfirmRehydration` callback SHALL signal via a `TaskCompletionSource<RestoreCostEstimate>` that it has a question. The CLI event loop (which is awaiting the pipeline) SHALL detect this, render cost tables and a `SelectionPrompt` on a clean console (no live display active), then set a response `TaskCompletionSource<RehydratePriority?>` to unblock the pipeline.
3. **Download phase** (step 7+): After confirmation returns, the CLI SHALL start `AnsiConsole.Progress()` with a determinate bar for files restored / total. The display loop SHALL use `Task.WhenAny(pipelineTask, Task.Delay(100))`.
4. **Cleanup confirmation**: When the pipeline invokes `ConfirmCleanup`, the progress display SHALL have been auto-cleared. The CLI SHALL render the cleanup prompt on a clean console.

#### Scenario: Cost tables render cleanly
- **WHEN** the restore pipeline invokes `ConfirmRehydration`
- **THEN** the cost summary table, cost breakdown table, and selection prompt SHALL render on a clean console without interference from any live display

#### Scenario: Download progress after confirmation
- **WHEN** the user selects a rehydration priority
- **THEN** a Spectre.Console `Progress` SHALL start showing files restored out of total

#### Scenario: Cleanup prompt renders cleanly
- **WHEN** `ConfirmCleanup` is invoked after all downloads complete
- **THEN** the progress display SHALL have been auto-cleared and the confirm prompt SHALL render without garbled output

#### Scenario: Pipeline completes without rehydration needed
- **WHEN** all chunks are available (no rehydration needed) and `ConfirmRehydration` is not invoked
- **THEN** the CLI SHALL show a progress bar for the download phase directly

### Requirement: Responsive poll loop
The archive and restore display poll loops SHALL use `Task.WhenAny(pipelineTask, Task.Delay(100))` instead of unconditional `await Task.Delay(100)` to respond immediately when the pipeline completes while still throttling the refresh rate during active operation.

#### Scenario: Pipeline finishes mid-delay
- **WHEN** the pipeline completes 10ms into a 100ms delay cycle
- **THEN** the display SHALL update and exit the loop immediately rather than waiting the remaining 90ms
