# CLI Spec

## Purpose

Defines the command-line interface for Arius, including verbs, options, option resolution, and DI handler registration.

## Requirements

### Requirement: CLI verbs
The system SHALL provide four CLI verbs using System.CommandLine: `archive`, `restore`, `ls`, `update`. Each verb (except `update`) SHALL accept common options: `--account` / `-a` (account name), `--key` / `-k` (account key), `--passphrase` / `-p` (optional, for encryption), `--container` / `-c` (blob container name). The `--account` option SHALL NOT be marked as required by System.CommandLine (it MAY be resolved from environment variables).

#### Scenario: Archive verb
- **WHEN** `arius archive /path/to/folder -a myaccount -k *** -c backup`
- **THEN** the system SHALL invoke the archive Mediator command with the provided options

#### Scenario: Restore verb
- **WHEN** `arius restore /photos/ -a myaccount -k *** -c backup`
- **THEN** the system SHALL invoke the restore Mediator command for the specified path

#### Scenario: Ls verb
- **WHEN** `arius ls -a myaccount -k *** -c backup`
- **THEN** the system SHALL invoke the ls Mediator command

#### Scenario: Long-form options accepted
- **WHEN** `arius ls --account myaccount --key *** --container backup`
- **THEN** the system SHALL invoke the ls Mediator command identically to the short-form equivalent

### Requirement: Archive-specific CLI options
The archive verb SHALL accept: `--tier` / `-t` (Hot/Cool/Cold/Archive, default Archive), `--remove-local` (delete binaries after archive), `--no-pointers` (skip pointer files). The tuning options `--small-file-threshold`, `--tar-target-size`, and `--dedup-cache-mb` SHALL NOT be exposed on the CLI; their defaults (1 MB, 64 MB, 512 MB respectively) SHALL be hardcoded internally.

#### Scenario: Custom tier
- **WHEN** `arius archive /path -t Hot -a a -k k -c c`
- **THEN** chunks SHALL be uploaded to Hot tier

#### Scenario: Default tier
- **WHEN** `arius archive /path -a a -k k -c c` (no `--tier` specified)
- **THEN** chunks SHALL be uploaded to Archive tier

#### Scenario: Remove local with no pointers rejected
- **WHEN** `arius archive /path --remove-local --no-pointers -a a -k k -c c`
- **THEN** the system SHALL reject the command with an error explaining the incompatibility and return exit code 1

### Requirement: Restore-specific CLI options
The restore verb SHALL accept: `-v` / `--version` (snapshot version, default latest), `--no-pointers` (skip pointer creation on restore), `--overwrite` (overwrite local files without prompting).

#### Scenario: Restore specific version with short flag
- **WHEN** `arius restore /photos/ -v 2026-03-21T140000.000Z -a a -k k -c c`
- **THEN** the system SHALL restore from the specified snapshot

#### Scenario: Restore specific version with long flag
- **WHEN** `arius restore /photos/ --version 2026-03-21T140000.000Z -a a -k k -c c`
- **THEN** the system SHALL restore from the specified snapshot

### Requirement: Ls-specific CLI options
The ls verb SHALL accept: `-v` / `--version` (snapshot version, default latest), `--prefix` (path prefix filter), `--filter` / `-f` (filename substring filter, case-insensitive).

#### Scenario: Ls with prefix and filter
- **WHEN** `arius ls --prefix photos/ -f .jpg -a a -k k -c c`
- **THEN** the system SHALL list files under `photos/` whose filename contains `.jpg`

### Requirement: Account key resolution
The CLI SHALL resolve the account key in the following order: (1) `--key` / `-k` CLI parameter, (2) `ARIUS_KEY` environment variable, (3) `Microsoft.Extensions.Configuration.UserSecrets` (hidden, developer use only). The key SHALL NEVER be logged or displayed in output. The `--key` option description SHALL NOT mention user secrets.

#### Scenario: Key from CLI parameter
- **WHEN** `-k` is provided on the command line
- **THEN** the system SHALL use that key for authentication

#### Scenario: Key from environment variable
- **WHEN** `-k` is not provided but `ARIUS_KEY` environment variable is set
- **THEN** the system SHALL use the environment variable value for authentication

#### Scenario: Key from user secrets
- **WHEN** neither `-k` nor `ARIUS_KEY` is available but a user secret is configured
- **THEN** the system SHALL resolve the key from user secrets

#### Scenario: Key not found
- **WHEN** no key is available from any source
- **THEN** the system SHALL report an error and exit with exit code 1

### Requirement: Account name resolution
The CLI SHALL resolve the account name in the following order: (1) `--account` / `-a` CLI parameter, (2) `ARIUS_ACCOUNT` environment variable. If neither is available, the system SHALL report an error and exit.

#### Scenario: Account from CLI parameter
- **WHEN** `-a myaccount` is provided on the command line
- **THEN** the system SHALL use `myaccount` as the storage account name

#### Scenario: Account from environment variable
- **WHEN** `-a` is not provided but `ARIUS_ACCOUNT=myaccount` environment variable is set
- **THEN** the system SHALL use `myaccount` as the storage account name

#### Scenario: Account not found
- **WHEN** neither `-a` nor `ARIUS_ACCOUNT` is available
- **THEN** the system SHALL report an error and exit with exit code 1

#### Scenario: CLI flag overrides environment variable
- **WHEN** `-a override` is provided and `ARIUS_ACCOUNT=envaccount` is set
- **THEN** the system SHALL use `override` as the storage account name

### Requirement: DI handler registration
The system SHALL register command handlers by their `ICommandHandler<TCommand, TResult>` interface using explicit factory delegates that pass `accountName` and `containerName` as constructor arguments. The `AddArius()` extension method in `Arius.Core` SHALL encapsulate all DI registration. Handler registrations MUST be placed after `AddMediator()` to override the source generator's auto-registrations.

#### Scenario: Successful DI resolution
- **WHEN** `AddArius()` is called with valid account, key, passphrase, and container
- **THEN** resolving `IMediator` and sending any command SHALL NOT throw a DI resolution exception

#### Scenario: Handler receives string parameters
- **WHEN** `AddArius("myaccount", "mykey", null, "mycontainer")` is called
- **THEN** the `ArchivePipelineHandler` SHALL be constructed with `accountName="myaccount"` and `containerName="mycontainer"`

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
  ● Scanning                              1523 files
  ○ Hashing                               640/1523
  ○ Uploading                             3/11 chunks
```

Symbols:
- `[green]●[/]` (U+25CF) — stage complete
- `[yellow]○[/]` (U+25CB) — stage in progress
- `[dim]○[/]` or `[grey]  [/]` (two spaces) — stage not yet started

- Scanning: dim placeholder until `TotalFiles` is known, then `[green]●[/]` with count
- Hashing: `[yellow]○[/]` with `FilesHashed / TotalFiles`, or `[green]●[/]` when `FilesHashed == TotalFiles`
- Uploading: `[yellow]○[/]` with `ChunksUploaded / TotalChunks` (or `ChunksUploaded chunks...` when `TotalChunks` unknown), or `[green]●[/]` when complete

**Per-file lines** (below stage headers, appear/disappear based on TrackedFile state):
```
  ...s/march/video.mp4   ████████░░░░  Hashing       62%  3.1 GB / 5.0 GB
  ...ments/data.db       ██████░░░░░░  Hashing       28%  560.0 MB / 2.0 GB
  notes.txt                            Queued in TAR      1.0 KB
  config.yml                           Queued in TAR      512 B
  readme.md              ████░░░░░░░░  Uploading TAR 33%  850 B
```

- File name column: `TruncateAndLeftJustify(file.RelativePath, 30)` then `Markup.Escape()`
- Progress bar column: 12-char Markup bar for Hashing/Uploading states; blank (`"".PadRight(12)`) for QueuedInTar/UploadingTar
- State label column: fixed-width state name
- Percentage column: present for Hashing/Uploading states only
- Size column: `BytesProcessed.Bytes().Humanize() + " / " + TotalBytes.Bytes().Humanize()` for Hashing/Uploading; `TotalBytes.Bytes().Humanize()` for QueuedInTar/UploadingTar

Files in `Done` state are excluded from the output entirely.

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
- **THEN** only stage headers SHALL be shown (all with `●`)

### Requirement: TruncateAndLeftJustify helper
The CLI SHALL expose an `internal static string TruncateAndLeftJustify(string input, int width)` helper with the following rules:
- If `input.Length <= width`: return `input.PadRight(width)`
- If `input.Length > width`: return `"..." + input[^(width - 3)..].PadRight(width)`

The caller is responsible for applying `Markup.Escape()` to the result before embedding in a Markup string. Input is the full relative path (forward-slash separated), not just the filename.

#### Scenario: Short path — no truncation
- **WHEN** `TruncateAndLeftJustify("notes.txt", 30)` is called
- **THEN** the result SHALL be `"notes.txt" + 21 spaces` (length 30)

#### Scenario: Long path — left truncation with ellipsis
- **WHEN** `TruncateAndLeftJustify("photos/2026/march/IMG_1234.jpg", 30)` is called and the path is 30 chars
- **THEN** the result SHALL be exactly 30 characters

#### Scenario: Very long path — ellipsis prefix
- **WHEN** `TruncateAndLeftJustify("a/very/deeply/nested/path/to/some/file.txt", 30)` is called
- **THEN** the result SHALL start with `"..."` and have total length 30

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

### Requirement: Restore progress display with Live and TCS phase coordination
The CLI SHALL use `AnsiConsole.Live()` + `BuildRestoreDisplay(ProgressState) → IRenderable` for both restore download phases (Phase 1 and Phase 3). The `AnsiConsole.Progress()` blocks and `UpdateRestoreTask()` helper SHALL be removed. The TCS phase coordination structure (4 phases, two TCS pairs) is otherwise unchanged.

The restore flow SHALL have distinct phases:

1. **Plan phase** (pipeline steps 1-6): No live progress display.
2. **Cost confirmation**: TCS-coordinated rendering of cost tables and selection prompt on clean console.
3. **Download phase** (step 7+): `AnsiConsole.Live()` with `BuildRestoreDisplay` for files restored / total.
4. **Cleanup confirmation**: Live display exits, cleanup prompt rendered on clean console.

#### Scenario: Cost tables render cleanly
- **WHEN** the restore pipeline invokes `ConfirmRehydration`
- **THEN** the cost tables and prompt SHALL render on a clean console without interference from any live display

#### Scenario: Pipeline completes without rehydration needed
- **WHEN** all chunks are available and `ConfirmRehydration` is not invoked
- **THEN** the CLI SHALL show a Live restore display for the download phase directly

### Requirement: BuildRestoreDisplay pure function
`BuildRestoreDisplay(ProgressState state) → IRenderable` SHALL be a pure function returning a `Rows(...)` renderable with:

**Stage header** (4 lines):
```
  ○ Restoring    650/1000 files
    Restored:    600  (2.3 GB)
    Skipped:     50   (150.0 MB)
    Rehydrating: 7 chunks (1.0 MB)
```

- `[yellow]○[/]` in progress, `[green]●[/]` when `FilesRestored + FilesSkipped == RestoreTotalFiles`
- Rehydrating line shown only when `RehydrationChunkCount > 0`
- Byte totals use Humanizer `.Bytes().Humanize()`

**Tail lines** (the 10 most recent `RestoreFileEvent` entries from `RecentRestoreEvents`):
```
  [green]●[/] ...tos/2026/march/IMG_1231.jpg  (1.2 MB)
  [green]●[/] ...tos/2026/march/IMG_1232.jpg  (3.4 MB)
  [dim]○[/] ...tos/2026/march/IMG_1233.jpg  (500 KB)
  [green]●[/] ...tos/2026/march/IMG_1234.jpg  (2.1 MB)
```

- `[green]●[/]` for restored (`Skipped = false`), `[dim]○[/]` for skipped (`Skipped = true`)
- Path column: `TruncateAndLeftJustify(path, 40)` then `Markup.Escape()`
- Size: `fileSize.Bytes().Humanize()` in parentheses
- On completion (all files done): tail lines are omitted; only the stage header is shown with `[green]●[/]`

#### Scenario: In-progress restore display
- **WHEN** 650 of 1000 files are processed (600 restored, 50 skipped) and the last 4 events are known
- **THEN** the display SHALL show the stage header with correct counts/bytes and a tail of the 4 most recent file events

#### Scenario: Completed restore display
- **WHEN** `FilesRestored + FilesSkipped == RestoreTotalFiles`
- **THEN** the display SHALL show `[green]●[/] Restoring 1000/1000 files` header with byte totals, and NO tail lines

### Requirement: Streaming progress events from Core
Arius.Core SHALL emit progress events via Mediator notifications. Event types SHALL include: FileScanned, FileHashing (with byte progress), FileHashed (with dedup result), ChunkUploading (with byte progress), ChunkUploaded, TarBundleSealing, TarBundleUploaded, SnapshotCreated, and equivalent restore events. The CLI SHALL subscribe to these events to drive the display.

#### Scenario: Progress event emission
- **WHEN** a file is hashed during archive
- **THEN** Core SHALL emit FileHashing events with bytes processed and FileHashed with the result

#### Scenario: CLI subscription
- **WHEN** Core emits a ChunkUploaded event
- **THEN** the CLI SHALL update the upload progress counter in the Spectre.Console display
