# Progress Display Spec

## Purpose

Defines the in-process progress state management for Arius CLI: the `ProgressState` class, per-file `TrackedFile` state machine, notification handlers wired to Core pipeline events, and restore-specific progress tracking. This capability lives in `Arius.Cli` and mediates between Core pipeline events and the Spectre.Console display functions.

## Requirements

### Requirement: Mediator handler discovery
The CLI assembly SHALL have `Mediator.SourceGenerator` and `Mediator.Abstractions` as direct package references so the source generator discovers `INotificationHandler<T>` implementations in `Arius.Cli`. All archive and restore notification handlers SHALL be invoked when Core publishes events.

#### Scenario: Handler invoked on publish
- **WHEN** Core publishes `FileScannedEvent` via `_mediator.Publish()`
- **THEN** the `FileScannedHandler` in `Arius.Cli` SHALL be invoked and update `ProgressState` (incrementing `FilesScanned` and `BytesScanned`)

#### Scenario: Production DI wiring
- **WHEN** `BuildProductionServices` creates the service provider
- **THEN** all notification handlers SHALL be resolvable by the source-generated Mediator

### Requirement: Per-file state machine in ProgressState
The `ProgressState` class SHALL track each file through a unified lifecycle using a `ConcurrentDictionary<string, TrackedFile>` keyed by relative path. `TrackedFile` SHALL contain:

- `RelativePath` (string) — the file's relative path, set at creation
- `ContentHash` (string?, volatile) — set when hashing completes
- `State` (FileState enum, volatile) — current lifecycle state
- `TotalBytes` (long) — file size, set at creation
- `BytesProcessed` (long, Interlocked-updated) — for hashing/uploading progress

The `FileState` enum SHALL have values: `Hashing`, `Hashed`, `Uploading`, `Done`.

- `Hashing` — file is being hashed, visible in per-file display with byte-level progress
- `Hashed` — hashing complete, invisible in per-file display; entry remains for `ContentHashToPath` lookup
- `Uploading` — large file upload in progress, visible in per-file display with byte-level progress
- `Done` — processing complete, entry about to be removed

The `TarId` field SHALL be removed from `TrackedFile`. TAR bundle tracking is handled by the separate `TrackedTar` entity.

#### Scenario: Large file lifecycle
- **WHEN** `FileHashingEvent("video.mp4", 5GB)` is published
- **THEN** a `TrackedFile` entry SHALL be added with `State = Hashing`
- **WHEN** `FileHashedEvent("video.mp4", "abc123")` is published
- **THEN** `ContentHash` SHALL be set to `"abc123"` and `State` SHALL transition to `Hashed`
- **WHEN** `ChunkUploadingEvent("abc123", 5GB)` is published
- **THEN** `State` SHALL transition to `Uploading` and `BytesProcessed` SHALL be reset to 0
- **WHEN** `ChunkUploadedEvent("abc123", 4GB)` is published
- **THEN** the entry SHALL be removed from the dictionary

#### Scenario: Small file lifecycle (tar path)
- **WHEN** `FileHashingEvent("notes.txt", 1KB)` is published
- **THEN** a `TrackedFile` entry SHALL be added with `State = Hashing`
- **WHEN** `FileHashedEvent("notes.txt", "def456")` is published
- **THEN** `ContentHash` SHALL be set to `"def456"` and `State` SHALL transition to `Hashed` (invisible in display)
- **WHEN** `TarEntryAddedEvent("def456", 3, 15KB)` is published
- **THEN** the `TrackedFile` entry SHALL be removed from the dictionary (small file subsumed into TAR bundle)

#### Scenario: Hashed state is invisible in display
- **WHEN** a `TrackedFile` has `State = Hashed`
- **THEN** it SHALL NOT appear in the per-file display area
- **AND** it SHALL remain in the `TrackedFiles` dictionary for `ContentHashToPath` lookup

#### Scenario: Byte-level progress update during hashing
- **WHEN** the `IProgress<long>` callback for a hashing file reports 2.5 GB read
- **THEN** `TrackedFile.BytesProcessed` SHALL be updated to 2,500,000,000 via `Interlocked.Exchange`

#### Scenario: Byte-level progress update during uploading
- **WHEN** the `IProgress<long>` callback for an uploading chunk reports 450 MB read
- **THEN** the corresponding `TrackedFile.BytesProcessed` SHALL be reset and updated for the upload phase

#### Scenario: Thread safety under contention
- **WHEN** 4 hash workers and 4 upload workers concurrently add, update, and transition entries
- **THEN** no data races SHALL occur and aggregate counters SHALL remain correct

### Requirement: ContentHash to RelativePath reverse lookup
`ProgressState` SHALL maintain a `ConcurrentDictionary<string, ConcurrentBag<string>>` mapping `ContentHash` to one or more `RelativePath` values. This mapping SHALL be populated when `FileHashedEvent` fires (which provides both `RelativePath` and `ContentHash`). Multiple files with identical content SHALL all be recorded under the same content hash key. Handlers for events keyed by content hash (e.g., `TarEntryAddedEvent`, `ChunkUploadingEvent`, `ChunkUploadedEvent`) SHALL use this mapping to locate the corresponding `TrackedFile` entries.

#### Scenario: Reverse lookup for tar entry
- **WHEN** `TarEntryAddedEvent("def456", 3, 15KB)` is published
- **THEN** the handler SHALL look up `"def456"` in the reverse map to find all `RelativePath` values, then remove each matching `TrackedFile` entry

#### Scenario: Reverse lookup populated before downstream events
- **WHEN** `FileHashedEvent("notes.txt", "def456")` is published
- **THEN** the reverse map SHALL contain `"def456" → ["notes.txt"]` BEFORE any `TarEntryAddedEvent` or `ChunkUploadingEvent` for `"def456"` can arrive (guaranteed by pipeline ordering)

#### Scenario: Multiple files with same content hash
- **WHEN** `FileHashedEvent("a.txt", "abc")` and `FileHashedEvent("b.txt", "abc")` are published
- **THEN** the reverse map SHALL contain `"abc" → ["a.txt", "b.txt"]`

### Requirement: Stage aggregate counters
`ProgressState` SHALL maintain aggregate counters for stage header display:

- `FilesScanned` (long, Interlocked-incremented by `FileScannedEvent`) — count of files discovered during enumeration
- `BytesScanned` (long, Interlocked-incremented by `FileScannedEvent`) — total bytes of files discovered
- `ScanComplete` (bool, set by `ScanCompleteEvent`) — true when enumeration finishes
- `TotalFiles` (long?, set by `ScanCompleteEvent`) — final file count
- `TotalBytes` (long?, set by `ScanCompleteEvent`) — final total bytes
- `FilesHashed` (long, incremented by `FileHashedEvent`)
- `FilesUnique` (long, Interlocked-incremented) — count of files that passed dedup and need uploading
- `ChunksUploaded` (long, incremented by `ChunkUploadedEvent`)
- `TotalChunks` (long?, set when dedup completes)
- `BytesUploaded` (long, incremented by `ChunkUploadedEvent`)
- `TarsUploaded` (long, incremented by `TarBundleUploadedEvent`)
- `SnapshotComplete` (bool, set by `SnapshotCreatedEvent`)
- `HashQueueDepth` (`Func<int>?`) — getter for hash channel pending count, set via `OnHashQueueReady` callback
- `UploadQueueDepth` (`Func<int>?`) — getter for upload channel pending count, set via `OnUploadQueueReady` callback

#### Scenario: Per-file scanning events tick up counter
- **WHEN** `FileScannedEvent("photos/img.jpg", 1200000)` is published
- **THEN** `ProgressState.FilesScanned` SHALL be incremented by 1 and `ProgressState.BytesScanned` SHALL be incremented by 1,200,000

#### Scenario: ScanCompleteEvent sets totals
- **WHEN** `ScanCompleteEvent(TotalFiles: 1523, TotalBytes: 5000000000)` is published
- **THEN** `ProgressState.TotalFiles` SHALL be set to 1523, `ProgressState.TotalBytes` SHALL be set to 5,000,000,000, and `ProgressState.ScanComplete` SHALL be set to true

#### Scenario: FilesUnique incremented for large file upload
- **WHEN** `ChunkUploadingEvent("abc123", 5GB)` is published for a large file (found in `TrackedFiles`)
- **THEN** `ProgressState.FilesUnique` SHALL be incremented by 1

#### Scenario: FilesUnique incremented for small file in tar
- **WHEN** `TarEntryAddedEvent("def456", 3, 15KB)` is published
- **THEN** `ProgressState.FilesUnique` SHALL be incremented by 1

#### Scenario: Queue depth getter stored
- **WHEN** the pipeline calls `OnHashQueueReady` with a `Func<int>` getter
- **THEN** `ProgressState.HashQueueDepth` SHALL be set to that getter and callable during display rendering

### Requirement: Archive notification handlers
Handlers SHALL be thin (state transition / counter increment only, no business logic). Each handler SHALL update the `TrackedFile` / `TrackedTar` state machine and/or aggregate counters on `ProgressState`:

| Event | TrackedFile action | TrackedTar action | Aggregate action |
|-------|-------------------|-------------------|------------------|
| `FileScannedEvent` | (none) | (none) | Increment `FilesScanned`, add `FileSize` to `BytesScanned` |
| `ScanCompleteEvent` | (none) | (none) | Set `TotalFiles`, `TotalBytes`, `ScanComplete` |
| `FileHashingEvent` | Add entry, `State = Hashing` | (none) | (none) |
| `FileHashedEvent` | Set `ContentHash`, `State = Hashed`, populate reverse map | (none) | Increment `FilesHashed` |
| `TarBundleStartedEvent` | (none) | Create new `TrackedTar`, `State = Accumulating` | (none) |
| `TarEntryAddedEvent` | Remove entry (small file done) | Update `FileCount` + `AccumulatedBytes` on current tar | Increment `FilesUnique` |
| `TarBundleSealingEvent` | (none) | `State → Sealing`, set `TarHash` + `TotalBytes` | (none) |
| `ChunkUploadingEvent` | If large file: `State → Uploading`, reset `BytesProcessed` | If tar: `State → Uploading` | Increment `FilesUnique` (large file only) |
| `ChunkUploadedEvent` | Remove entry (large file) | (none) | Increment `ChunksUploaded`, add `BytesUploaded` |
| `TarBundleUploadedEvent` | (none) | Remove `TrackedTar` entry | Increment `TarsUploaded`, increment `ChunksUploaded` |
| `SnapshotCreatedEvent` | (none) | (none) | Set `SnapshotComplete` |

The `ChunkUploadingHandler` SHALL perform a dual lookup: first check `TrackedFiles` (via `ContentHashToPath` reverse map) for large files, then check `TrackedTars` (via `TarHash` match) for TAR bundles. Only one lookup SHALL match for any given content hash.

#### Scenario: TarBundleStartedEvent creates tracked tar
- **WHEN** `TarBundleStartedEvent()` is published
- **THEN** the handler SHALL create a new `TrackedTar` with a sequential `BundleNumber`, `State = Accumulating`, and `TargetSize` set to `TarTargetSize` (64 MB default)

#### Scenario: TarEntryAddedEvent updates tar and removes file
- **WHEN** `TarEntryAddedEvent("def456", 3, 15KB)` is published
- **THEN** the handler SHALL update the current `TrackedTar`'s `FileCount` and `AccumulatedBytes`, remove the `TrackedFile` entry for `"def456"` via reverse lookup, and increment `FilesUnique`

#### Scenario: ChunkUploadingEvent routes to large file
- **WHEN** `ChunkUploadingEvent("abc123", 5GB)` is published and `"abc123"` is found in `ContentHashToPath`
- **THEN** the handler SHALL transition the matching `TrackedFile` to `State = Uploading`, reset `BytesProcessed` to 0, and increment `FilesUnique`

#### Scenario: ChunkUploadingEvent routes to tar bundle
- **WHEN** `ChunkUploadingEvent("tarhash1", 50MB)` is published and `"tarhash1"` matches a `TrackedTar.TarHash`
- **THEN** the handler SHALL transition the matching `TrackedTar` to `State = Uploading`

#### Scenario: TarBundleUploadedEvent removes tar
- **WHEN** `TarBundleUploadedEvent("tarhash1", 40MB, 64)` is published
- **THEN** the handler SHALL remove the `TrackedTar` with matching `TarHash`

### Requirement: TrackedTar display entity
`ProgressState` SHALL maintain a `ConcurrentDictionary<int, TrackedTar>` keyed by `BundleNumber` for tracking TAR bundle lifecycle in the display. `TrackedTar` SHALL contain:

- `BundleNumber` (int) — sequential display-only identifier, assigned by CLI handler
- `State` (TarState enum) — current lifecycle state
- `FileCount` (int) — number of files added so far
- `AccumulatedBytes` (long) — cumulative uncompressed bytes of files added
- `TargetSize` (long) — `TarTargetSize` value (default 64 MB), for accumulation progress bar
- `TotalBytes` (long) — final uncompressed size, set at sealing
- `BytesUploaded` (long, Interlocked-updated) — cumulative bytes uploaded, for upload progress bar
- `TarHash` (string?) — set at sealing, used for upload progress lookup

The `TarState` enum SHALL have values: `Accumulating`, `Sealing`, `Uploading`.

- `Accumulating` — tar is accepting file entries; display shows accumulation progress bar (`AccumulatedBytes / TargetSize`)
- `Sealing` — tar is being sealed and hashed; display shows bar frozen at last accumulation value
- `Uploading` — tar is being uploaded; display shows upload progress bar (`BytesUploaded / TotalBytes`)

Bundle numbering SHALL be a CLI-only concern. Core events SHALL NOT carry bundle numbers.

#### Scenario: TrackedTar lifecycle
- **WHEN** `TarBundleStartedEvent()` is published
- **THEN** a `TrackedTar` SHALL be created with `BundleNumber` = next sequential number, `State = Accumulating`, `TargetSize = TarTargetSize`
- **WHEN** `TarEntryAddedEvent` events update the tar
- **THEN** `FileCount` and `AccumulatedBytes` SHALL be updated on the current (most recent) `TrackedTar`
- **WHEN** `TarBundleSealingEvent(5, 100KB, "tarhash", [...])` is published
- **THEN** the current `TrackedTar`'s `State` SHALL transition to `Sealing`, `TarHash` SHALL be set, and `TotalBytes` SHALL be set
- **WHEN** `ChunkUploadingEvent("tarhash", ...)` is published matching the `TarHash`
- **THEN** `State` SHALL transition to `Uploading`
- **WHEN** `TarBundleUploadedEvent("tarhash", ...)` is published
- **THEN** the `TrackedTar` SHALL be removed

#### Scenario: Accumulation progress bar target
- **WHEN** a `TrackedTar` is in `Accumulating` state with `AccumulatedBytes = 32MB` and `TargetSize = 64MB`
- **THEN** the accumulation progress bar SHALL show 50% fill

#### Scenario: Upload byte-level progress via ProgressStream
- **WHEN** `CreateUploadProgress` is called with a content hash matching a `TrackedTar.TarHash`
- **THEN** the returned `IProgress<long>` SHALL update `TrackedTar.BytesUploaded` via `Interlocked.Exchange`

#### Scenario: Final tar partially filled
- **WHEN** the last TAR seals with `AccumulatedBytes` well below `TargetSize` (e.g., 3 MB of 64 MB)
- **THEN** the accumulation bar SHALL have shown low fill, and the tar SHALL proceed normally through `Sealing` and `Uploading`

#### Scenario: Concurrent tars in different states
- **WHEN** TAR #1 is `Uploading`, TAR #2 is `Sealing`, and TAR #3 is `Accumulating`
- **THEN** all three SHALL appear in the display simultaneously with their respective progress indicators

### Requirement: Restore notification handlers
The system SHALL implement `INotificationHandler<T>` for all restore notification events. Each handler SHALL update the corresponding fields on `ProgressState`:

| Handler | Action |
|---------|--------|
| `RestoreStartedHandler` | Call `state.SetRestoreTotalFiles(count)` |
| `FileRestoredHandler` | Call `state.IncrementFilesRestored(fileSize)`, `state.AddRestoreEvent(path, size, skipped: false)`. Remove `TrackedDownload` for large files |
| `FileSkippedHandler` | Call `state.IncrementFilesSkipped(fileSize)`, `state.AddRestoreEvent(path, size, skipped: true)` |
| `RehydrationStartedHandler` | Call `state.SetRehydration(chunkCount, totalBytes)` |
| `SnapshotResolvedHandler` | Set `state.SnapshotTimestamp` and `state.SnapshotRootHash` |
| `TreeTraversalCompleteHandler` | Set `state.RestoreTotalFiles`, `state.RestoreTotalOriginalSize`, `state.TreeTraversalComplete` |
| `TreeTraversalProgressHandler` | Set `state.RestoreFilesDiscovered` from batched progress event |
| `FileDispositionHandler` | Increment the appropriate `Disposition*` tally based on the event's `Disposition` enum value |
| `ChunkResolutionCompleteHandler` | Set `state.ChunkGroups`, `state.LargeChunkCount`, `state.TarChunkCount`, update `TotalOriginalBytes` and `TotalCompressedBytes` |
| `RehydrationStatusHandler` | Set `state.ChunksAvailable`, `state.ChunksRehydrated`, `state.ChunksNeedingRehydration`, `state.ChunksPending` |
| `ChunkDownloadStartedHandler` | For tar bundles, store `(fileCount, originalSize)` in `state.TarBundleMetadata[chunkHash]` for display label construction |
| `ChunkDownloadCompletedHandler` | Remove `TrackedDownload` entry and increment `state.RestoreBytesDownloaded` |
| `CleanupCompleteHandler` | Reserved for future cleanup display — currently no-op |

#### Scenario: RestoreStartedEvent sets total
- **WHEN** `RestoreStartedEvent(TotalFiles: 1000)` is published
- **THEN** `ProgressState.RestoreTotalFiles` SHALL be set to 1000

#### Scenario: FileRestoredEvent increments counter and bytes
- **WHEN** `FileRestoredEvent("photos/img.jpg", FileSize: 1_200_000)` is published
- **THEN** `ProgressState.FilesRestored` SHALL be incremented by 1
- **AND** `ProgressState.BytesRestored` SHALL be incremented by 1,200,000
- **AND** a `RestoreFileEvent("photos/img.jpg", 1_200_000, Skipped: false)` SHALL be enqueued to `RecentRestoreEvents`

#### Scenario: FileSkippedEvent increments counter and bytes
- **WHEN** `FileSkippedEvent("photos/img.jpg", FileSize: 500_000)` is published
- **THEN** `ProgressState.FilesSkipped` SHALL be incremented by 1
- **AND** `ProgressState.BytesSkipped` SHALL be incremented by 500,000
- **AND** a `RestoreFileEvent("photos/img.jpg", 500_000, Skipped: true)` SHALL be enqueued

#### Scenario: RehydrationStartedEvent stores bytes
- **WHEN** `RehydrationStartedEvent(ChunkCount: 7, TotalBytes: 1_048_576)` is published
- **THEN** `ProgressState.RehydrationChunkCount` SHALL be set to 7
- **AND** `ProgressState.RehydrationTotalBytes` SHALL be set to 1,048,576

#### Scenario: SnapshotResolvedEvent sets snapshot fields
- **WHEN** `SnapshotResolvedEvent(2026-03-28T14:00:00Z, "abc123", 9)` is published
- **THEN** `ProgressState.SnapshotTimestamp` SHALL be set to `2026-03-28T14:00:00Z` and `ProgressState.SnapshotRootHash` SHALL be set to `"abc123"`

#### Scenario: FileDispositionEvent increments tally
- **WHEN** `FileDispositionEvent("photo.jpg", RestoreDisposition.New, 1_200_000)` is published
- **THEN** `ProgressState.DispositionNew` SHALL be incremented by 1

#### Scenario: TreeTraversalCompleteEvent sets totals and file count
- **WHEN** `TreeTraversalCompleteEvent(FileCount: 9, TotalOriginalSize: 6_910_000)` is published
- **THEN** `ProgressState.RestoreTotalFiles` SHALL be set to 9
- **AND** `ProgressState.RestoreTotalOriginalSize` SHALL be set to 6,910,000
- **AND** `ProgressState.TreeTraversalComplete` SHALL be set to true

### Requirement: Restore per-file tracking in ProgressState
`ProgressState` SHALL track restore progress at the file level for display in the restore tail:

**`RestoreFileEvent` record** (internal):
```
RestoreFileEvent(string RelativePath, long FileSize, bool Skipped)
```

**New fields on `ProgressState`**:
- `BytesRestored` (long, Interlocked) — total bytes of files written to disk
- `BytesSkipped` (long, Interlocked) — total bytes of files skipped
- `RehydrationTotalBytes` (long, Interlocked) — total bytes for rehydration, from `RehydrationStartedEvent`
- `RecentRestoreEvents` (`ConcurrentQueue<RestoreFileEvent>`) — rolling window capped at 10 entries
- `RestoreFilesDiscovered` (long, Interlocked) — files found during tree traversal
- `RestoreTotalCompressedBytes` (long) — total compressed download bytes from chunk resolution
- `RestoreBytesDownloaded` (long, Interlocked) — cumulative compressed bytes downloaded
- `TrackedDownloads` (`ConcurrentDictionary<string, TrackedDownload>`) — active downloads

**Updated / new methods**:
- `IncrementFilesRestored(long fileSize)` — increments `FilesRestored` and adds `fileSize` to `BytesRestored`
- `IncrementFilesSkipped(long fileSize)` — increments `FilesSkipped` and adds `fileSize` to `BytesSkipped`
- `SetRehydration(int count, long bytes)` — sets `RehydrationChunkCount` and `RehydrationTotalBytes`
- `AddRestoreEvent(string path, long size, bool skipped)` — enqueues to `RecentRestoreEvents`; if count would exceed 10, dequeue the oldest entry first

#### Scenario: Ring buffer caps at 10
- **WHEN** 15 `FileRestoredEvent` notifications arrive
- **THEN** `RecentRestoreEvents` SHALL contain exactly 10 entries (the 10 most recent)

#### Scenario: Thread safety of ring buffer
- **WHEN** multiple restore workers concurrently enqueue events
- **THEN** `RecentRestoreEvents` SHALL never contain more than 10 entries and no entries SHALL be lost beyond the cap

#### Scenario: TrackedDownloads visible during downloads
- **WHEN** 3 chunks are being downloaded in parallel
- **THEN** `TrackedDownloads` SHALL contain 3 entries, each with current `BytesDownloaded` values

### Requirement: Restore disposition tracking in ProgressState
`ProgressState` SHALL track disposition tallies for the restore display:

- `DispositionNew` (int, Interlocked) — count of files with disposition New
- `DispositionSkipIdentical` (int, Interlocked) — count of files with disposition SkipIdentical
- `DispositionOverwrite` (int, Interlocked) — count of files with disposition Overwrite
- `DispositionKeepLocalDiffers` (int, Interlocked) — count of files with disposition KeepLocalDiffers

These SHALL be incremented by the `FileDispositionHandler`.

#### Scenario: Disposition tallies accumulate correctly
- **WHEN** 5 files have disposition New, 3 have SkipIdentical, 1 has Overwrite, and 2 have KeepLocalDiffers
- **THEN** `DispositionNew` SHALL be 5, `DispositionSkipIdentical` SHALL be 3, `DispositionOverwrite` SHALL be 1, and `DispositionKeepLocalDiffers` SHALL be 2

#### Scenario: Thread-safe disposition increment
- **WHEN** multiple disposition events arrive concurrently
- **THEN** all tallies SHALL be correctly incremented with no data races

### Requirement: Restore snapshot and tree tracking in ProgressState
`ProgressState` SHALL track snapshot resolution and tree traversal information:

- `SnapshotTimestamp` (DateTimeOffset?) — set by `SnapshotResolvedHandler`
- `SnapshotRootHash` (string?) — set by `SnapshotResolvedHandler`
- `RestoreTotalOriginalSize` (long) — total uncompressed size of all files in the tree, set by `TreeTraversalCompleteHandler`
- `TreeTraversalComplete` (bool) — set to true by `TreeTraversalCompleteHandler`

#### Scenario: Snapshot resolved sets fields
- **WHEN** `SnapshotResolvedEvent(2026-03-28T14:00:00Z, "abc123", 9)` is published
- **THEN** `SnapshotTimestamp` SHALL be set to the timestamp and `SnapshotRootHash` SHALL be set to `"abc123"`

#### Scenario: Tree traversal sets total size
- **WHEN** `TreeTraversalCompleteEvent(9, 6_910_000)` is published
- **THEN** `RestoreTotalOriginalSize` SHALL be set to 6,910,000 and `TreeTraversalComplete` SHALL be true

### Requirement: Restore chunk and rehydration tracking in ProgressState
`ProgressState` SHALL track chunk resolution and rehydration status:

- `ChunkGroups` (int) — set by `ChunkResolutionCompleteHandler`
- `LargeChunkCount` (int) — set by `ChunkResolutionCompleteHandler`
- `TarChunkCount` (int) — set by `ChunkResolutionCompleteHandler`
- `ChunksAvailable` (int) — set by `RehydrationStatusHandler`
- `ChunksRehydrated` (int) — set by `RehydrationStatusHandler`
- `ChunksNeedingRehydration` (int) — set by `RehydrationStatusHandler`
- `ChunksPending` (int) — set by `RehydrationStatusHandler`

#### Scenario: Chunk resolution sets counts
- **WHEN** `ChunkResolutionCompleteEvent(5, 2, 3)` is published
- **THEN** `ChunkGroups` SHALL be 5, `LargeChunkCount` SHALL be 2, and `TarChunkCount` SHALL be 3

#### Scenario: Rehydration status sets availability
- **WHEN** `RehydrationStatusEvent(3, 2, 1, 0)` is published
- **THEN** `ChunksAvailable` SHALL be 3, `ChunksRehydrated` SHALL be 2, `ChunksNeedingRehydration` SHALL be 1, and `ChunksPending` SHALL be 0

### Requirement: TrackedDownload state entity
`ProgressState` SHALL maintain a `ConcurrentDictionary<string, TrackedDownload>` keyed by chunk hash for tracking active restore downloads. `TrackedDownload` SHALL contain:

- `Key` (string) -- chunk hash, used as dictionary key
- `Kind` (`DownloadKind` enum) -- `LargeFile` or `TarBundle`
- `DisplayName` (string) -- file relative path for large files, `"TAR bundle (N files, X)"` for tar bundles
- `CompressedSize` (long) -- total compressed download size in bytes
- `BytesDownloaded` (long, Interlocked-updated) -- cumulative bytes downloaded, for progress bar
- `OriginalSize` (long) -- sum of original file sizes for this chunk (for aggregate tracking)

`TrackedDownload` SHALL have a single implicit state: present in the dictionary means downloading, removed means done.

#### Scenario: Large file tracked download lifecycle
- **WHEN** `CreateDownloadProgress("photos/sunset.jpg", 25_400_000, LargeFile)` is called
- **THEN** a `TrackedDownload` SHALL be added with `Key` = chunk hash, `Kind = LargeFile`, `DisplayName = "photos/sunset.jpg"`, `CompressedSize = 25_400_000`
- **WHEN** `FileRestoredEvent("photos/sunset.jpg", ...)` is published
- **THEN** the `TrackedDownload` SHALL be removed from the dictionary

#### Scenario: Tar bundle tracked download lifecycle
- **WHEN** `CreateDownloadProgress("ab12cd34", 15_200_000, TarBundle)` is called with metadata (3 files, 847 KB original)
- **THEN** a `TrackedDownload` SHALL be added with `Kind = TarBundle`, `DisplayName = "TAR bundle (3 files, 847 KB)"`
- **WHEN** `ChunkDownloadCompletedEvent("ab12cd34", ...)` is published
- **THEN** the `TrackedDownload` SHALL be removed from the dictionary

#### Scenario: Byte-level progress update during download
- **WHEN** the `IProgress<long>` callback reports 12,300,000 bytes downloaded
- **THEN** `TrackedDownload.BytesDownloaded` SHALL be updated to 12,300,000 via `Interlocked.Exchange`

#### Scenario: Thread safety with 4 concurrent downloads
- **WHEN** 4 parallel workers concurrently add, update, and remove `TrackedDownload` entries
- **THEN** no data races SHALL occur and the dictionary SHALL remain consistent

### Requirement: Tree traversal resolving counter
`ProgressState` SHALL maintain a `RestoreFilesDiscovered` counter (long, Interlocked-updated) that tracks the number of files found during tree traversal. This counter SHALL be updated by a handler for `TreeTraversalProgressEvent`. It is used by the display to show `○ Resolving  N files...` during tree traversal.

#### Scenario: Counter ticks up during traversal
- **WHEN** `TreeTraversalProgressEvent(FilesFound: 523)` is published
- **THEN** `ProgressState.RestoreFilesDiscovered` SHALL be set to 523

#### Scenario: Counter reaches final value
- **WHEN** tree traversal completes with 1,247 files
- **THEN** `RestoreFilesDiscovered` SHALL equal 1,247 and `TreeTraversalComplete` SHALL be true

### Requirement: Restore aggregate byte totals from chunk resolution
`ProgressState` SHALL maintain `RestoreTotalCompressedBytes` (long) and update `RestoreTotalOriginalSize` from `ChunkResolutionCompleteEvent`. These SHALL be set by a handler for the enriched `ChunkResolutionCompleteEvent` which now carries `TotalOriginalBytes` and `TotalCompressedBytes`.

`RestoreTotalCompressedBytes` SHALL serve as the denominator for the aggregate download progress bar. `RestoreTotalOriginalSize` SHALL be displayed as context alongside the download progress.

#### Scenario: Chunk resolution provides byte totals
- **WHEN** `ChunkResolutionCompleteEvent(10, 5, 5, TotalOriginalBytes: 500_000_000, TotalCompressedBytes: 200_000_000)` is published
- **THEN** `ProgressState.RestoreTotalOriginalSize` SHALL be set to 500,000,000
- **AND** `ProgressState.RestoreTotalCompressedBytes` SHALL be set to 200,000,000

### Requirement: Restore download bytes counter
`ProgressState` SHALL maintain a `RestoreBytesDownloaded` counter (long, Interlocked-updated) that tracks the cumulative compressed bytes downloaded across all chunks. This SHALL be incremented when each chunk download completes (from `FileRestoredEvent` for large files, `ChunkDownloadCompletedEvent` for tar bundles) using the chunk's `CompressedSize`.

This counter drives the aggregate progress bar numerator.

#### Scenario: Download bytes accumulate across chunks
- **WHEN** a 25 MB large file chunk completes, then a 15 MB tar chunk completes
- **THEN** `RestoreBytesDownloaded` SHALL be 40,000,000

### Requirement: Restore notification handlers for new events
The system SHALL implement `INotificationHandler<T>` for the new restore events:

| Handler | Action |
|---------|--------|
| `TreeTraversalProgressHandler` | Set `state.RestoreFilesDiscovered` to `FilesFound` |
| `ChunkDownloadCompletedHandler` | Remove `TrackedDownload` entry by chunk hash, add `CompressedSize` to `RestoreBytesDownloaded` |

The existing `ChunkResolutionCompleteHandler` SHALL be updated to also set `RestoreTotalOriginalSize` and `RestoreTotalCompressedBytes` from the enriched event.

The existing `FileRestoredHandler` SHALL be updated to also remove the corresponding `TrackedDownload` entry (for large files) and add the chunk's `CompressedSize` to `RestoreBytesDownloaded`.

#### Scenario: TreeTraversalProgressHandler updates counter
- **WHEN** `TreeTraversalProgressEvent(523)` is published
- **THEN** `RestoreFilesDiscovered` SHALL be 523

#### Scenario: ChunkDownloadCompletedHandler cleans up tracked download
- **WHEN** `ChunkDownloadCompletedEvent("ab12cd34", 3, 15_200_000)` is published
- **THEN** the `TrackedDownload` with key `"ab12cd34"` SHALL be removed
- **AND** `RestoreBytesDownloaded` SHALL be incremented by 15,200,000

#### Scenario: FileRestoredHandler cleans up tracked download for large file
- **WHEN** `FileRestoredEvent("photos/sunset.jpg", 50_000_000)` is published for a large file download
- **THEN** the corresponding `TrackedDownload` SHALL be removed
- **AND** `RestoreBytesDownloaded` SHALL be incremented by the chunk's `CompressedSize`

### Requirement: Restore display layout
`BuildRestoreDisplay` SHALL render the restore progress in three stages with progressive detail. The display SHALL replace the completed-file tail with an active-download table showing in-flight downloads.

**Stage 1: Resolving/Resolved**
- During tree traversal: `○ Resolving  N files...` where N is `RestoreFilesDiscovered`
- After traversal: `● Resolved  <timestamp> (N files)` initially without size
- After chunk resolution: `● Resolved  <timestamp> (N files, X)` with total original size appended

**Stage 2: Checking/Checked**
- Before any dispositions: `○ Checking`
- During conflict check: `○ Checking  N new, M identical, ...` with ticking counters
- After chunk resolution or first download: `● Checked  N new, M identical, O overwrite, P kept`

**Stage 3: Restoring**
- Before any downloads: `○ Restoring`
- During downloads: `○ Restoring  done/total files  ████████░░░░░░░░  N%  (X / Y download, Z original)`
  - The progress bar and percentage track compressed download bytes (`RestoreBytesDownloaded / RestoreTotalCompressedBytes`)
  - `X / Y download` shows compressed bytes downloaded vs total
  - `Z original` shows total original file size
- After all downloads: `● Restoring  done/total files  ████████████████ 100%  (Y download, Z original)`

**Active download table** (replaces completed-file tail):
- Shown when there are active `TrackedDownload` entries and not all files are done
- Each row shows: display name, progress bar, percentage, current/total bytes
- Large files: display name is the file's relative path (truncated)
- Tar bundles: display name is `TAR bundle (N files, X)` where X is humanized original size
- Reuses `RenderProgressBar`, `SplitSizePair`, `TruncateAndLeftJustify` from archive display
- Same borderless table layout as archive per-file rows

#### Scenario: Display during tree traversal
- **WHEN** tree traversal has found 523 files and is still running
- **THEN** the display SHALL show `○ Resolving  523 files...` with `○ Checking` and `○ Restoring` below

#### Scenario: Display after chunk resolution
- **WHEN** chunk resolution completes with 847 files to restore, 14.2 GB original, 8.31 GB compressed
- **THEN** the Resolved line SHALL show `● Resolved  <ts> (1,247 files, 14.2 GB)`
- **AND** the Checked line SHALL show `● Checked  847 new, 400 identical, 0 overwrite, 0 kept`
- **AND** the Restoring line SHALL show `○ Restoring  0/847 files  ░░░░░░░░░░░░░░░░  0%  (0 B / 8.31 GB download, 14.2 GB original)`

#### Scenario: Display during active downloads
- **WHEN** 312 of 847 files are done, 3.17 GB of 8.31 GB compressed downloaded, with 2 active downloads
- **THEN** the Restoring line SHALL show the aggregate progress bar at 38%
- **AND** the active download table SHALL show 2 rows with per-item progress bars

#### Scenario: Large file in active download table
- **WHEN** `vacation/photos/sunset.jpg` is 72% downloaded (18.3 / 25.4 MB)
- **THEN** the row SHALL show `vacation/photos/sunset.jpg` (truncated), progress bar at 72%, and `18.3 / 25.4 MB`

#### Scenario: Tar bundle in active download table
- **WHEN** a tar bundle containing 3 files (847 KB original) is 31% downloaded (4.8 / 15.2 MB compressed)
- **THEN** the row SHALL show `TAR bundle (3 files, 847 KB)`, progress bar at 31%, and `4.8 / 15.2 MB`

#### Scenario: Active downloads disappear when complete
- **WHEN** a download finishes
- **THEN** the `TrackedDownload` SHALL be removed and the row SHALL disappear from the active download table
- **AND** the aggregate counters SHALL update to reflect the completed download

#### Scenario: Display on completion
- **WHEN** all 847 files are restored
- **THEN** all three stage indicators SHALL show `●`
- **AND** the active download table SHALL be empty (not rendered)
- **AND** the Restoring line SHALL show `● Restoring  847/847 files  ████████████████ 100%  (8.31 GB download, 14.2 GB original)`
