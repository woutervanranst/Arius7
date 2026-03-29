## ADDED Requirements

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

## MODIFIED Requirements

### Requirement: Restore notification handlers
The system SHALL implement `INotificationHandler<T>` for all restore notification events. Each handler SHALL update the corresponding fields on `ProgressState`:

| Handler | Action |
|---------|--------|
| `RestoreStartedHandler` | Call `state.SetRestoreTotalFiles(count)` |
| `FileRestoredHandler` | Call `state.IncrementFilesRestored(fileSize)`, `state.AddRestoreEvent(path, size, skipped: false)`, remove `TrackedDownload` for the chunk, add `CompressedSize` to `RestoreBytesDownloaded` |
| `FileSkippedHandler` | Call `state.IncrementFilesSkipped(fileSize)`, `state.AddRestoreEvent(path, size, skipped: true)` |
| `RehydrationStartedHandler` | Call `state.SetRehydration(chunkCount, totalBytes)` |
| `SnapshotResolvedHandler` | Set `SnapshotTimestamp`, `RestoreTotalFiles` (from initial file count) |
| `TreeTraversalCompleteHandler` | Set `TreeTraversalComplete = true` |
| `TreeTraversalProgressHandler` | Set `RestoreFilesDiscovered` to `FilesFound` |
| `FileDispositionHandler` | Increment disposition counters (`DispositionNew`, `DispositionSkipIdentical`, etc.) |
| `ChunkResolutionCompleteHandler` | Set `ChunkGroups`, `RestoreTotalOriginalSize`, `RestoreTotalCompressedBytes` |
| `ChunkDownloadStartedHandler` | (reserved -- metadata for `TrackedDownload` is set via `CreateDownloadProgress` callback) |
| `ChunkDownloadCompletedHandler` | Remove `TrackedDownload`, add `CompressedSize` to `RestoreBytesDownloaded` |

#### Scenario: RestoreStartedEvent sets total
- **WHEN** `RestoreStartedEvent(TotalFiles: 1000)` is published
- **THEN** `ProgressState.RestoreTotalFiles` SHALL be set to 1000

#### Scenario: FileRestoredEvent increments counter, bytes, and cleans up download
- **WHEN** `FileRestoredEvent("photos/img.jpg", FileSize: 1_200_000)` is published
- **THEN** `ProgressState.FilesRestored` SHALL be incremented by 1
- **AND** `ProgressState.BytesRestored` SHALL be incremented by 1,200,000
- **AND** a `RestoreFileEvent("photos/img.jpg", 1_200_000, Skipped: false)` SHALL be enqueued to `RecentRestoreEvents`
- **AND** if a `TrackedDownload` exists for this file's chunk, it SHALL be removed

#### Scenario: FileSkippedEvent increments counter and bytes
- **WHEN** `FileSkippedEvent("photos/img.jpg", FileSize: 500_000)` is published
- **THEN** `ProgressState.FilesSkipped` SHALL be incremented by 1
- **AND** `ProgressState.BytesSkipped` SHALL be incremented by 500,000
- **AND** a `RestoreFileEvent("photos/img.jpg", 500_000, Skipped: true)` SHALL be enqueued

#### Scenario: RehydrationStartedEvent stores bytes
- **WHEN** `RehydrationStartedEvent(ChunkCount: 7, TotalBytes: 1_048_576)` is published
- **THEN** `ProgressState.RehydrationChunkCount` SHALL be set to 7
- **AND** `ProgressState.RehydrationTotalBytes` SHALL be set to 1,048,576

### Requirement: Restore per-file tracking in ProgressState
`ProgressState` SHALL track restore progress at the file level for display in the restore tail:

**`RestoreFileEvent` record** (internal):
```
RestoreFileEvent(string RelativePath, long FileSize, bool Skipped)
```

**New fields on `ProgressState`**:
- `BytesRestored` (long, Interlocked) -- total bytes of files written to disk
- `BytesSkipped` (long, Interlocked) -- total bytes of files skipped
- `RehydrationTotalBytes` (long, Interlocked) -- total bytes for rehydration, from `RehydrationStartedEvent`
- `RecentRestoreEvents` (`ConcurrentQueue<RestoreFileEvent>`) -- rolling window capped at 10 entries
- `RestoreFilesDiscovered` (long, Interlocked) -- files found during tree traversal
- `RestoreTotalCompressedBytes` (long) -- total compressed download bytes from chunk resolution
- `RestoreBytesDownloaded` (long, Interlocked) -- cumulative compressed bytes downloaded
- `TrackedDownloads` (`ConcurrentDictionary<string, TrackedDownload>`) -- active downloads

**Updated / new methods**:
- `IncrementFilesRestored(long fileSize)` -- increments `FilesRestored` and adds `fileSize` to `BytesRestored`
- `IncrementFilesSkipped(long fileSize)` -- increments `FilesSkipped` and adds `fileSize` to `BytesSkipped`
- `SetRehydration(int count, long bytes)` -- sets `RehydrationChunkCount` and `RehydrationTotalBytes`
- `AddRestoreEvent(string path, long size, bool skipped)` -- enqueues to `RecentRestoreEvents`; if count would exceed 10, dequeue the oldest entry first

#### Scenario: Ring buffer caps at 10
- **WHEN** 15 `FileRestoredEvent` notifications arrive
- **THEN** `RecentRestoreEvents` SHALL contain exactly 10 entries (the 10 most recent)

#### Scenario: Thread safety of ring buffer
- **WHEN** multiple restore workers concurrently enqueue events
- **THEN** `RecentRestoreEvents` SHALL never contain more than 10 entries and no entries SHALL be lost beyond the cap

#### Scenario: TrackedDownloads visible during downloads
- **WHEN** 3 chunks are being downloaded in parallel
- **THEN** `TrackedDownloads` SHALL contain 3 entries, each with current `BytesDownloaded` values

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
