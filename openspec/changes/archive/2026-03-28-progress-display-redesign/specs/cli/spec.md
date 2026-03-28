## MODIFIED Requirements

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

## REMOVED Requirements

### Requirement: Mediator handler discovery
**Reason**: The scenario referencing the old `FileScannedEvent(TotalFiles)` signature is outdated. The requirement itself (handler discovery) is still valid but the scenario needs to reference the new `FileScannedEvent(string RelativePath, long FileSize)` signature.
**Migration**: The requirement is re-added via the MODIFIED `Mediator handler discovery` requirement below.

## MODIFIED Requirements

### Requirement: Mediator handler discovery
The CLI assembly SHALL have `Mediator.SourceGenerator` and `Mediator.Abstractions` as direct package references so the source generator discovers `INotificationHandler<T>` implementations in `Arius.Cli`. All archive and restore notification handlers SHALL be invoked when Core publishes events.

#### Scenario: Handler invoked on publish
- **WHEN** Core publishes `FileScannedEvent` via `_mediator.Publish()`
- **THEN** the `FileScannedHandler` in `Arius.Cli` SHALL be invoked and update `ProgressState` (incrementing `FilesScanned` and `BytesScanned`)

#### Scenario: Production DI wiring
- **WHEN** `BuildProductionServices` creates the service provider
- **THEN** all notification handlers SHALL be resolvable by the source-generated Mediator
