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
The CLI SHALL resolve the account key in the following order: (1) `--key` / `-k` CLI parameter, (2) `ARIUS_KEY` environment variable, (3) `Microsoft.Extensions.Configuration.UserSecrets` (hidden, developer use only). The key SHALL NEVER be logged or displayed in output. The `--key` option description SHALL NOT mention user secrets. Credential resolution (key lookup and fallback to `AzureCliCredential`) SHALL remain in `Arius.Cli`. The resolved credential SHALL be passed to `BlobServiceFactory.CreateAsync` as either a `StorageSharedKeyCredential` or a `TokenCredential`.

#### Scenario: Key from CLI parameter
- **WHEN** `-k` is provided on the command line
- **THEN** the system SHALL create a `StorageSharedKeyCredential` and pass it to `BlobServiceFactory.CreateAsync`

#### Scenario: Key from environment variable
- **WHEN** `-k` is not provided but `ARIUS_KEY` environment variable is set
- **THEN** the system SHALL create a `StorageSharedKeyCredential` from the environment variable and pass it to `BlobServiceFactory.CreateAsync`

#### Scenario: Key from user secrets
- **WHEN** neither `-k` nor `ARIUS_KEY` is available but a user secret is configured
- **THEN** the system SHALL resolve the key from user secrets, create a `StorageSharedKeyCredential`, and pass it to `BlobServiceFactory.CreateAsync`

#### Scenario: No key available — fallback to AzureCliCredential
- **WHEN** no key is available from any source
- **THEN** the system SHALL create an `AzureCliCredential` and pass it to `BlobServiceFactory.CreateAsync`

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
The system SHALL register command handlers by their `ICommandHandler<TCommand, TResult>` interface using explicit factory delegates that pass `accountName` and `containerName` as constructor arguments. The `AddArius()` extension method in `Arius.Core` SHALL encapsulate all DI registration. Handler registrations MUST be placed after `AddMediator()` to override the source generator's auto-registrations. `CliBuilder.BuildProductionServices` SHALL delegate blob service construction and preflight validation to `BlobServiceFactory.CreateAsync` and SHALL only be responsible for credential resolution, calling the factory, and wiring the DI container with the returned `IBlobStorageService`.

#### Scenario: Successful DI resolution
- **WHEN** `AddArius()` is called with a valid `IBlobStorageService` from `BlobServiceFactory.CreateAsync`
- **THEN** resolving `IMediator` and sending any command SHALL NOT throw a DI resolution exception

#### Scenario: BuildProductionServices delegates to factory
- **WHEN** `BuildProductionServices` is called with an account name, key, passphrase, container, and preflight mode
- **THEN** it SHALL resolve the credential, call `BlobServiceFactory.CreateAsync`, and build the DI container with the returned service

#### Scenario: Handler receives string parameters
- **WHEN** `AddArius("myaccount", "mykey", null, "mycontainer")` is called
- **THEN** the `ArchivePipelineHandler` SHALL be constructed with `accountName="myaccount"` and `containerName="mycontainer"`

### Requirement: CLI formats PreflightException from structured fields
The CLI verb catch blocks SHALL format user-facing error messages by switching on `PreflightException.ErrorKind` and using the structured fields (`AccountName`, `ContainerName`, `AuthMode`, `StatusCode`) rather than displaying `PreflightException.Message` directly. Each verb SHALL format messages appropriate to its preflight mode (e.g., archive suggests "Storage Blob Data Contributor" role, restore/ls suggest "Storage Blob Data Reader" role). The CLI SHALL NOT have direct dependencies on Azure SDK namespaces (`Azure.Storage`, `Azure.Identity`, `Azure.Core`, `Azure`); all Azure interactions SHALL be mediated through `Arius.AzureBlob` types.

#### Scenario: ContainerNotFound error formatted
- **WHEN** a verb catches `PreflightException` with `ErrorKind = ContainerNotFound`
- **THEN** the CLI SHALL display a message including the container name and account name from the structured fields

#### Scenario: AccessDenied with key auth formatted
- **WHEN** a verb catches `PreflightException` with `ErrorKind = AccessDenied` and `AuthMode = "key"`
- **THEN** the CLI SHALL display a message suggesting the account key may be incorrect

#### Scenario: AccessDenied with token auth formatted for archive
- **WHEN** the archive verb catches `PreflightException` with `ErrorKind = AccessDenied` and `AuthMode = "token"`
- **THEN** the CLI SHALL display a message suggesting the "Storage Blob Data Contributor" RBAC role

#### Scenario: AccessDenied with token auth formatted for restore
- **WHEN** the restore verb catches `PreflightException` with `ErrorKind = AccessDenied` and `AuthMode = "token"`
- **THEN** the CLI SHALL display a message suggesting the "Storage Blob Data Reader" RBAC role

#### Scenario: CredentialUnavailable error formatted
- **WHEN** a verb catches `PreflightException` with `ErrorKind = CredentialUnavailable`
- **THEN** the CLI SHALL display a message listing the credential resolution options (--key, ARIUS_KEY, user secrets, az login)

#### Scenario: CLI has no direct Azure SDK dependencies
- **WHEN** `Arius.Cli` is analyzed for type dependencies
- **THEN** no class in `Arius.Cli` SHALL depend on types in `Azure.Storage`, `Azure.Identity`, or `Azure.Core` namespaces

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
The `BuildArchiveDisplay` function SHALL return a `Rows(...)` renderable with three sections:

**Stage headers** (persistent summary lines at top):
```
  ● Scanning   1.523 files
  ○ Hashing    720 / 1.523 files (312 unique)          [12 pending]
  ○ Uploading  3 unique chunks                         [2 pending]
```

Symbols:
- `[green]●[/]` (U+25CF) — stage complete
- `[yellow]○[/]` (U+25CB) — stage in progress
- `[dim]○[/]` or `[grey]  [/]` (two spaces) — stage not yet started

- Scanning: `[yellow]○[/]` with `FilesScanned` ticking up during enumeration. `[green]●[/]` with final `TotalFiles` count when `ScanComplete` is true.
- Hashing: `[yellow]○[/]` with `FilesHashed / TotalFiles` (or `FilesHashed files...` when `TotalFiles` unknown). Shows `(N unique)` suffix with `FilesUnique` count. Shows `[N pending]` dimmed suffix when `HashQueueDepth` returns > 0. `[green]●[/]` when `FilesHashed == TotalFiles`.
- Uploading: `[yellow]○[/]` with `ChunksUploaded unique chunks` (or `ChunksUploaded / TotalChunks` when `TotalChunks` known). Shows `[N pending]` dimmed suffix when `UploadQueueDepth` returns > 0. Only shown when there is upload activity. `[green]●[/]` when complete.

**Per-file lines** (only `TrackedFile` entries where `State is Hashing or Uploading`):
```
  ...rview-v2 - WouterNotes.pptx  ██████░░░░░░  Hashing    50%  6,67 / 13,34 MB
  ...FY14 - EMS Plan.pptx         ████████████  Uploading 100%  6,39 / 6,39 MB
```

- File name column: `TruncateAndLeftJustify(file.RelativePath, 30)` then `Markup.Escape()`
- Progress bar column: 12-char Markup bar for Hashing/Uploading states
- State label column: fixed-width state name
- Percentage column: present for Hashing/Uploading states
- Size column: `BytesProcessed.Bytes().Humanize() + " / " + TotalBytes.Bytes().Humanize()`

Files in `Hashed` or `Done` state SHALL NOT appear in the per-file area.

**TAR bundle lines** (all `TrackedTar` entries from `ProgressState.TrackedTars`):
```
  TAR #1 (23 files, 5,1 MB)       ███░░░░░░░░░  Accumulating    5,1 / 64 MB
  TAR #2 (64 files, 47,8 MB)      ████████████  Sealing        47,8 / 64 MB
  TAR #3 (64 files, 52,1 MB)      ██████████░░  Uploading  83%  43,2 / 52,1 MB
```

- Name column: `TAR #N (M files, X MB)` where N is `BundleNumber`, M is `FileCount`, X is `AccumulatedBytes` humanized
- Progress bar column: 12-char Markup bar
  - `Accumulating`: fill = `AccumulatedBytes / TargetSize`
  - `Sealing`: bar frozen at last accumulation ratio
  - `Uploading`: fill = `BytesUploaded / TotalBytes`
- State label column: `Accumulating`, `Sealing`, or `Uploading`
- Size column: progress bytes / target or total bytes

#### Scenario: Full archive display with TAR bundles
- **WHEN** scanning is complete with 1523 files, 720 hashed (312 unique), 2 files actively hashing, 1 file uploading, TAR #1 accumulating, TAR #2 uploading
- **THEN** the display SHALL show stage headers with correct counts/dedup/queue depths, per-file lines for the 2 hashing and 1 uploading file, and TAR lines for both bundles

#### Scenario: Scanning counter ticks up live
- **WHEN** enumeration is in progress and 500 of (unknown total) files have been scanned
- **THEN** the scanning header SHALL show `[yellow]○[/] Scanning 500 files...` (ticking up with each `FileScannedEvent`)

#### Scenario: Queue depth shown when non-zero
- **WHEN** `HashQueueDepth` returns 12 and `UploadQueueDepth` returns 2
- **THEN** the hashing header SHALL include `[dim][12 pending][/]` and the uploading header SHALL include `[dim][2 pending][/]`

#### Scenario: Queue depth hidden when zero
- **WHEN** `HashQueueDepth` returns 0
- **THEN** the hashing header SHALL NOT show any `[N pending]` suffix

#### Scenario: Dedup count shown on hashing header
- **WHEN** `FilesUnique` is 312 and `FilesHashed` is 720
- **THEN** the hashing header SHALL show `720 / 1.523 files (312 unique)`

#### Scenario: File completes hashing and disappears
- **WHEN** a file transitions from `Hashing` to `Hashed`
- **THEN** the file's per-file line SHALL NOT appear in the next display tick

#### Scenario: TAR bundle removed after upload
- **WHEN** `TarBundleUploadedEvent` fires for TAR #1
- **THEN** TAR #1's line SHALL NOT appear in the next display tick

#### Scenario: Empty display between phases
- **WHEN** all `TrackedFile` entries are in `Hashed`/`Done` state and no `TrackedTar` entries exist
- **THEN** only stage headers SHALL be shown

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
The CLI SHALL inject `IProgress<long>` callbacks into Core via `ArchiveOptions.CreateHashProgress` and `ArchiveOptions.CreateUploadProgress`. The CLI SHALL also wire `ArchiveOptions.OnHashQueueReady` and `ArchiveOptions.OnUploadQueueReady` to store the queue depth getters in `ProgressState`.

The `CreateHashProgress` factory SHALL look up the corresponding `TrackedFile` entry in `ProgressState` and return an `IProgress<long>` that updates `TrackedFile.BytesProcessed` via `Interlocked.Exchange`.

The `CreateUploadProgress` factory SHALL perform a dual lookup:
1. First check `TrackedFiles` via the `ContentHash → RelativePath` reverse map (for large file uploads)
2. Then check `TrackedTars` by matching `TarHash` (for TAR bundle uploads)
Only one lookup SHALL match for any given content hash (TAR hashes and content hashes are hashes of different content, so collisions are impossible).

For large files, the returned `IProgress<long>` SHALL update `TrackedFile.BytesProcessed`. For TAR bundles, it SHALL update `TrackedTar.BytesUploaded`.

#### Scenario: Hash progress callback
- **WHEN** Core calls `CreateHashProgress("video.mp4", 5GB)`
- **THEN** the factory SHALL look up the `TrackedFile` for `"video.mp4"` and return an `IProgress<long>` that sets its `BytesProcessed`

#### Scenario: Upload progress callback for large file
- **WHEN** Core calls `CreateUploadProgress("abc123", 5GB)` and `"abc123"` is found in `ContentHashToPath`
- **THEN** the factory SHALL find the `TrackedFile` and return an `IProgress<long>` that sets its `BytesProcessed`

#### Scenario: Upload progress callback for TAR bundle
- **WHEN** Core calls `CreateUploadProgress("tarhash1", 52MB)` and `"tarhash1"` matches a `TrackedTar.TarHash`
- **THEN** the factory SHALL find the `TrackedTar` and return an `IProgress<long>` that sets its `BytesUploaded`

#### Scenario: Upload progress callback with no match
- **WHEN** Core calls `CreateUploadProgress` with a hash that matches neither a `TrackedFile` nor a `TrackedTar`
- **THEN** the factory SHALL return a no-op `IProgress<long>`

#### Scenario: Queue depth callbacks wired
- **WHEN** the CLI creates `ArchiveOptions`
- **THEN** `OnHashQueueReady` SHALL be set to store the getter in `ProgressState.HashQueueDepth`
- **AND** `OnUploadQueueReady` SHALL be set to store the getter in `ProgressState.UploadQueueDepth`

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

**TCS deadlock fix**: After ANY live display loop exits AND `pipelineTask` is not yet complete, the CLI SHALL check whether `cleanupQuestionTcs.Task.IsCompleted` is true. If so, the CLI SHALL handle the cleanup prompt (ask user, set `cleanupAnswerTcs`) before awaiting `pipelineTask`. This check SHALL apply in ALL code paths — both the "rehydration needed" path and the "no rehydration needed" path — to prevent the deadlock where the pipeline awaits `cleanupAnswerTcs` while the CLI awaits `pipelineTask`.

The simplified structure after any live loop:
```
if (!pipelineTask.IsCompleted && cleanupQuestionTcs.Task.IsCompleted)
{
    // Handle cleanup prompt
}
await pipelineTask;
```

#### Scenario: Cost tables render cleanly
- **WHEN** the restore pipeline invokes `ConfirmRehydration`
- **THEN** the cost tables and prompt SHALL render on a clean console without interference from any live display

#### Scenario: Pipeline completes without rehydration needed
- **WHEN** all chunks are available and `ConfirmRehydration` is not invoked
- **THEN** the CLI SHALL show a Live restore display for the download phase directly

#### Scenario: TCS deadlock prevented — no rehydration, cleanup needed
- **WHEN** no rehydration is needed (questionTcs never fires) but the pipeline invokes `ConfirmCleanup`
- **THEN** the CLI SHALL detect that `cleanupQuestionTcs.Task.IsCompleted` is true, handle the cleanup prompt, and set `cleanupAnswerTcs` before awaiting `pipelineTask`
- **AND** the pipeline SHALL NOT hang indefinitely

#### Scenario: TCS deadlock prevented — post-rehydration download, cleanup needed
- **WHEN** the download live loop exits and `pipelineTask` is not yet complete because the pipeline is awaiting `cleanupAnswerTcs`
- **THEN** the CLI SHALL check `cleanupQuestionTcs.Task.IsCompleted` and handle cleanup before awaiting `pipelineTask`

### Requirement: BuildRestoreDisplay pure function
`BuildRestoreDisplay(ProgressState state) → IRenderable` SHALL be a pure function returning a `Rows(...)` renderable with:

**Stage headers** (three stages, always shown):

```
  ● Resolved     2026-03-28T14:00:00.0000000+00:00 (9,224 files, 5.16 GB)
  ● Checked      4,613 new, 4,601 identical, 0 overwrite, 10 kept
  ○ Restoring    4,867/9,224 files  █████░░░░░░░░░░░  32%
                 (1.57 / 4.92 GB download, 5.16 GB original)
```

Stage 1 — **Resolved / Resolving**:
- During tree traversal: `[dim]○[/] Resolving    N files...` where N is `RestoreFilesDiscovered` (`:N0` formatted). If no files discovered yet, no count shown.
- After traversal (`TreeTraversalComplete`): `[green]●[/] Resolved     <timestamp> (N files)` initially without size.
- After chunk resolution sets `RestoreTotalOriginalSize > 0`: `[green]●[/] Resolved     <timestamp> (N files, X)` with humanized total original size appended.
- Timestamp: `SnapshotTimestamp.Value.ToString("o")`, or `"?"` if null. The detail string is `Markup.Escape()`-d.

Stage 2 — **Checked**:
- `[dim]○[/]` when no dispositions yet, `[yellow]○[/]` during disposition checks, `[green]●[/]` when complete (detected when `ChunkGroups > 0` or `done > 0`).
- Shows tallies: `N new, N identical, N overwrite, N kept` (all `:N0` formatted).

Stage 3 — **Restoring** (two-line layout when byte totals are known):
- `[dim]○[/]` initially, `[yellow]○[/]` during downloads (`done > 0` or `RestoreBytesDownloaded > 0`), `[green]●[/]` when all files done.
- Line 1: `{symbol} Restoring    {done:N0}/{total:N0} files  {bar}  {pct}%` — progress bar (16 chars, `RenderProgressBar`) tracking compressed download bytes (`RestoreBytesDownloaded / RestoreTotalCompressedBytes`).
- Line 2 (indented 17 spaces): `[dim]({dlCur} / {dlTot} {dlUnit} download, {origStr} original)[/]` — dual byte counters via `SplitSizePair` for download and `Bytes().Humanize()` for original.
- When `RestoreTotalCompressedBytes` is 0 (no byte totals yet), only a single line is shown: `{symbol} Restoring    {done:N0} files` (or `{done:N0}/{total:N0} files` if total is known), with no progress bar or byte counters.

**Active download table** (shown when not all done AND `TrackedDownloads` is non-empty):
- Blank separator line, then a borderless Spectre `Table` (`NoBorder()`, `HideHeaders()`, `NoWrap()` columns) with 4 columns: name | bar | % | size.
- Row data is collected first to compute max widths for padding (same pattern as archive per-file display).
- Name: `TruncateAndLeftJustify(dl.DisplayName, 35)` then `Markup.Escape()`, rendered `[dim]`.
- Bar: `RenderProgressBar(fraction, 12)` tracking `BytesDownloaded / CompressedSize`.
- Percentage: right-aligned, `PadLeft(maxPct)`, rendered `[dim]`.
- Size: `SplitSizePair(BytesDownloaded, CompressedSize)` with `PadLeft` alignment, rendered `[dim]`.

**Tail lines** (shown when not all done AND no active downloads):
- The 10 most recent `RestoreFileEvent` entries from `RecentRestoreEvents`.
- Blank separator line, then each entry:
  ```
  {sym} [dim]{path}[/]  ({sizeStr})
  ```
- `[green]●[/]` for restored (`Skipped = false`), `[dim]○[/]` for skipped (`Skipped = true`).
- Path column: `TruncateAndLeftJustify(path, 40)` then `Markup.Escape()`.
- Size: `fileSize.Bytes().Humanize()` in parentheses, `Markup.Escape()`-d.

**On completion** (`FilesRestored + FilesSkipped >= RestoreTotalFiles`): the active download table and tail lines are both omitted; only the three stage headers remain, all with `[green]●[/]`.

#### Scenario: In-progress restore display with resolved and checked stages
- **WHEN** snapshot is resolved with 9 files totaling 6.91 MB, all dispositions checked (9 new), and 5 of 9 files restored
- **THEN** the display SHALL show Resolved as green bullet with snapshot info, Checked as green bullet with `9 new, 0 identical, 0 overwrite, 0 kept`, and Restoring as yellow with `5/9 files`

#### Scenario: Completed restore display
- **WHEN** `FilesRestored + FilesSkipped == RestoreTotalFiles`
- **THEN** the display SHALL show all three stages with `[green]●[/]` and NO tail lines

#### Scenario: Display before any events
- **WHEN** the live display starts but no events have been received yet
- **THEN** the display SHALL show all stages as `[dim]○[/]` with zeroed counts

### Requirement: Streaming progress events from Core
Arius.Core SHALL emit progress events via Mediator notifications. Event types SHALL include: FileScanned (per-file, with RelativePath and FileSize), ScanComplete (with TotalFiles and TotalBytes), FileHashing (with byte progress), FileHashed (with dedup result), TarBundleStarted (parameterless), TarEntryAdded, TarBundleSealing, ChunkUploading (with byte progress), ChunkUploaded, TarBundleUploaded, SnapshotCreated, and equivalent restore events. The CLI SHALL subscribe to these events to drive the display.

#### Scenario: Progress event emission
- **WHEN** a file is hashed during archive
- **THEN** Core SHALL emit FileHashing events with bytes processed and FileHashed with the result

#### Scenario: CLI subscription
- **WHEN** Core emits a ChunkUploaded event
- **THEN** the CLI SHALL update the upload progress counter in the Spectre.Console display

#### Scenario: Per-file scanning events
- **WHEN** files are being enumerated
- **THEN** Core SHALL emit `FileScannedEvent` per file (not a single batch event at the end)

#### Scenario: TAR lifecycle events
- **WHEN** a TAR bundle is being built
- **THEN** Core SHALL emit `TarBundleStartedEvent` at creation, `TarEntryAddedEvent` per file, `TarBundleSealingEvent` at seal, and `TarBundleUploadedEvent` after upload
