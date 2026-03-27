## REVISED Requirements (v2 — Live display approach)

### Requirement: Archive progress display with Spectre.Console Live
The CLI SHALL use `AnsiConsole.Live(renderable).StartAsync(...)` for the archive progress display when the terminal is interactive. The display SHALL be rebuilt every tick (100ms) by calling a pure function `BuildArchiveDisplay(ProgressState) → IRenderable` and passing the result to `ctx.UpdateTarget(...)`.

The Live display SHALL be configured with:
- `VerticalOverflow.Crop` — crop content that exceeds terminal height
- `VerticalOverflowCropping.Bottom` — keep stage headers at top visible, crop overflow from bottom
- `AutoClear(false)` — display persists after completion to show final state

The display SHALL NOT use Spectre.Console `Progress`, `ProgressTask`, or `ProgressContext` for the archive operation.

#### Scenario: Live display setup
- **WHEN** the archive command starts on an interactive terminal
- **THEN** the CLI SHALL create an `AnsiConsole.Live(...)` context and run the pipeline concurrently with the display poll loop

#### Scenario: Non-interactive terminal
- **WHEN** the terminal does not support interactive output (piped or CI)
- **THEN** the CLI SHALL fall back to running the pipeline with no visual progress display

### Requirement: Archive display layout
The `BuildArchiveDisplay` function SHALL return a `Rows(...)` renderable with two sections:

**Stage headers** (persistent summary lines at top):
```
  ✓ Scanning                              1523 files
  ◐ Hashing                               640/1523
  ◐ Uploading                             3/11 chunks
```

- Scanning: indeterminate spinner until `TotalFiles` is known, then `✓` with count
- Hashing: spinner with `FilesHashed / TotalFiles`, or `✓` when `FilesHashed == TotalFiles`
- Uploading: spinner with `ChunksUploaded / TotalChunks` (or `ChunksUploaded chunks...` when `TotalChunks` unknown), or `✓` when complete

**Per-file lines** (below stage headers, appear/disappear based on TrackedFile state):
```
  video.mp4      ████████░░░░  Hashing       62%  3.1/5.0 GB
  data.db        ██████░░░░░░  Hashing       28%  560M/2.0 GB
  notes.txt                    Queued in TAR
  config.yml                   Queued in TAR
  readme.md      ████░░░░░░░░  Uploading TAR 33%
```

Each line represents one `TrackedFile` entry (excluding `Done` state, which is removed before display). Progress bars are rendered as Markup text (e.g., `[green]████████[/][dim]░░░░░░░░[/]`). Lines for files in `QueuedInTar` state show no progress bar (there is no byte-level progress to show). Lines for files in `UploadingTar` state show the tar upload's byte-level progress (all files in the same tar show the same percentage).

#### Scenario: Full archive display
- **WHEN** 640 of 1523 files are hashed, 4 files are hashing with byte-level progress, 2 files queued in tar, 3 files in uploading tar, 3 of 11 chunks uploaded
- **THEN** the display SHALL show stage headers with correct counts and per-file lines for all active TrackedFile entries

#### Scenario: File completes and disappears
- **WHEN** a large file finishes uploading (`ChunkUploadedEvent`)
- **THEN** the `TrackedFile` entry SHALL be removed and the file's line SHALL not appear in the next display tick

#### Scenario: Tar batch completes and all files disappear
- **WHEN** a tar bundle finishes uploading (`TarBundleUploadedEvent`)
- **THEN** all `TrackedFile` entries in that tar SHALL be removed and their lines SHALL not appear in the next display tick

#### Scenario: Empty display between phases
- **WHEN** all files have been processed (all `TrackedFile` entries removed)
- **THEN** only stage headers SHALL be shown (all with `✓`)

### Requirement: Progress bar rendering
Per-file progress bars SHALL be rendered as Markup strings with a configurable width (default 12 characters). The filled portion SHALL use `[green]█[/]` characters and the empty portion SHALL use `[dim]░[/]` characters. The fill ratio SHALL be `BytesProcessed / TotalBytes`.

#### Scenario: 62% progress
- **WHEN** a file has `BytesProcessed = 3,100,000,000` and `TotalBytes = 5,000,000,000`
- **THEN** the progress bar SHALL render as approximately 7-8 filled characters and 4-5 empty characters (at width 12)

### Requirement: Archive progress callback wiring
The CLI SHALL inject `IProgress<long>` callbacks into Core via `ArchiveOptions.CreateHashProgress` and `ArchiveOptions.CreateUploadProgress`. These factory callbacks SHALL look up the corresponding `TrackedFile` entry in `ProgressState` and return an `IProgress<long>` that updates `TrackedFile.BytesProcessed` via `Interlocked.Exchange`.

#### Scenario: Hash progress callback
- **WHEN** Core calls `CreateHashProgress("video.mp4", 5GB)`
- **THEN** the factory SHALL look up the `TrackedFile` for `"video.mp4"` and return an `IProgress<long>` that sets its `BytesProcessed`

#### Scenario: Upload progress callback
- **WHEN** Core calls `CreateUploadProgress("abc123", 5GB)`
- **THEN** the factory SHALL use the `ContentHash → RelativePath` reverse map to find the `TrackedFile` and return an `IProgress<long>` that sets its `BytesProcessed`

### Requirement: Responsive poll loop
The archive display poll loop SHALL use `await Task.WhenAny(pipelineTask, Task.Delay(100, ct))` instead of unconditional `await Task.Delay(100)` to respond immediately when the pipeline completes while still throttling the refresh rate during active operation.

#### Scenario: Pipeline finishes mid-delay
- **WHEN** the pipeline completes 10ms into a 100ms delay cycle
- **THEN** the display SHALL update and exit the loop immediately rather than waiting the remaining 90ms

### Requirement: Restore progress display with TCS phase coordination
The CLI SHALL use `TaskCompletionSource` pairs to coordinate between the restore pipeline's callback invocations and the console display. This requirement is UNCHANGED from the previous design — the restore display uses `Spectre.Console.Progress` with a fixed set of tasks (no dynamic add/remove), which works correctly.

The restore flow SHALL have distinct phases:

1. **Plan phase** (pipeline steps 1-6): No live progress display.
2. **Cost confirmation**: TCS-coordinated rendering of cost tables and selection prompt on clean console.
3. **Download phase** (step 7+): `AnsiConsole.Progress()` with determinate bar for files restored / total.
4. **Cleanup confirmation**: Progress auto-clears, cleanup prompt rendered on clean console.

#### Scenario: Cost tables render cleanly
- **WHEN** the restore pipeline invokes `ConfirmRehydration`
- **THEN** the cost tables and prompt SHALL render on a clean console without interference from any live display

#### Scenario: Pipeline completes without rehydration needed
- **WHEN** all chunks are available and `ConfirmRehydration` is not invoked
- **THEN** the CLI SHALL show a progress bar for the download phase directly
