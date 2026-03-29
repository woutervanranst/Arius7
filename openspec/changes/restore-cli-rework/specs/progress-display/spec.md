## ADDED Requirements

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

## MODIFIED Requirements

### Requirement: Restore notification handlers
The system SHALL implement `INotificationHandler<T>` for all restore notification events. Each handler SHALL update the corresponding fields on `ProgressState`:

| Handler | Action |
|---------|--------|
| `RestoreStartedHandler` | Call `state.SetRestoreTotalFiles(count)` |
| `FileRestoredHandler` | Call `state.IncrementFilesRestored(fileSize)`, `state.AddRestoreEvent(path, size, skipped: false)` |
| `FileSkippedHandler` | Call `state.IncrementFilesSkipped(fileSize)`, `state.AddRestoreEvent(path, size, skipped: true)` |
| `RehydrationStartedHandler` | Call `state.SetRehydration(chunkCount, totalBytes)` |
| `SnapshotResolvedHandler` | Set `state.SnapshotTimestamp` and `state.SnapshotRootHash` |
| `TreeTraversalCompleteHandler` | Set `state.RestoreTotalFiles`, `state.RestoreTotalOriginalSize`, `state.TreeTraversalComplete` |
| `FileDispositionHandler` | Increment the appropriate `Disposition*` tally based on the event's `Disposition` enum value |
| `ChunkResolutionCompleteHandler` | Set `state.ChunkGroups`, `state.LargeChunkCount`, `state.TarChunkCount` |
| `RehydrationStatusHandler` | Set `state.ChunksAvailable`, `state.ChunksRehydrated`, `state.ChunksNeedingRehydration`, `state.ChunksPending` |
| `ChunkDownloadStartedHandler` | (reserved for future per-chunk progress — currently no-op or log only) |
| `CleanupCompleteHandler` | (reserved for future cleanup display — currently no-op or log only) |

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
