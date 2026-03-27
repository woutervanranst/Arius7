## REVISED Requirements (v2 — Live display approach)

### Requirement: Mediator handler discovery
The CLI assembly SHALL have `Mediator.SourceGenerator` and `Mediator.Abstractions` as direct package references so the source generator discovers `INotificationHandler<T>` implementations in `Arius.Cli`. All archive and restore notification handlers SHALL be invoked when Core publishes events.

#### Scenario: Handler invoked on publish
- **WHEN** Core publishes `FileScannedEvent` via `_mediator.Publish()`
- **THEN** the `FileScannedHandler` in `Arius.Cli` SHALL be invoked and update `ProgressState`

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
- `TarId` (string?, volatile) — set when assigned to a tar bundle

The `FileState` enum SHALL have values: `Hashing`, `QueuedInTar`, `UploadingTar`, `Uploading`, `Done`.

#### Scenario: Large file lifecycle
- **WHEN** `FileHashingEvent("video.mp4", 5GB)` is published
- **THEN** a `TrackedFile` entry SHALL be added with `State = Hashing`
- **WHEN** `FileHashedEvent("video.mp4", "abc123")` is published
- **THEN** `ContentHash` SHALL be set to `"abc123"`
- **WHEN** `ChunkUploadingEvent("abc123", 5GB)` is published
- **THEN** `State` SHALL transition to `Uploading`
- **WHEN** `ChunkUploadedEvent("abc123", 4GB)` is published
- **THEN** the entry SHALL be removed from the dictionary

#### Scenario: Small file lifecycle (tar path)
- **WHEN** `FileHashingEvent("notes.txt", 1KB)` is published
- **THEN** a `TrackedFile` entry SHALL be added with `State = Hashing`
- **WHEN** `FileHashedEvent("notes.txt", "def456")` is published
- **THEN** `ContentHash` SHALL be set to `"def456"`
- **WHEN** `TarEntryAddedEvent("def456", 3, 15KB)` is published
- **THEN** `State` SHALL transition to `QueuedInTar`
- **WHEN** `TarBundleSealingEvent(5, 100KB, ["def456", ...])` is published
- **THEN** `State` SHALL transition to `UploadingTar` and `TarId` SHALL be set
- **WHEN** `TarBundleUploadedEvent(tarHash, 80KB, 5)` is published
- **THEN** all entries with matching `TarId` SHALL be removed from the dictionary

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
`ProgressState` SHALL maintain a `ConcurrentDictionary<string, string>` mapping `ContentHash → RelativePath`. This mapping SHALL be populated when `FileHashedEvent` fires (which provides both `RelativePath` and `ContentHash`). Handlers for events keyed by content hash (e.g., `TarEntryAddedEvent`, `ChunkUploadingEvent`, `ChunkUploadedEvent`) SHALL use this mapping to locate the corresponding `TrackedFile` entry.

#### Scenario: Reverse lookup for tar entry
- **WHEN** `TarEntryAddedEvent("def456", 3, 15KB)` is published
- **THEN** the handler SHALL look up `"def456"` in the reverse map to find the `RelativePath`, then update the `TrackedFile` entry's state

#### Scenario: Reverse lookup populated before downstream events
- **WHEN** `FileHashedEvent("notes.txt", "def456")` is published
- **THEN** the reverse map SHALL contain `"def456" → "notes.txt"` BEFORE any `TarEntryAddedEvent` or `ChunkUploadingEvent` for `"def456"` can arrive (guaranteed by pipeline ordering)

### Requirement: Stage aggregate counters
`ProgressState` SHALL continue to maintain aggregate counters for stage header display:

- `TotalFiles` (long?, set by `FileScannedEvent`)
- `FilesHashed` (long, incremented by `FileHashedEvent`)
- `ChunksUploaded` (long, incremented by `ChunkUploadedEvent`)
- `TotalChunks` (long?, set when dedup completes)
- `BytesUploaded` (long, incremented by `ChunkUploadedEvent`)
- `TarsUploaded` (long, incremented by `TarBundleUploadedEvent`)
- `SnapshotComplete` (bool, set by `SnapshotCreatedEvent`)

These are used by the stage header lines in the display, independent of the per-file `TrackedFile` state.

#### Scenario: FileScannedEvent sets total
- **WHEN** `FileScannedEvent(TotalFiles: 1523)` is published
- **THEN** `ProgressState.TotalFiles` SHALL be set to 1523

### Requirement: Archive notification handlers
Handlers SHALL be thin (state transition / counter increment only, no business logic). Each handler SHALL update the `TrackedFile` state machine and/or aggregate counters on `ProgressState`:

| Event | TrackedFile action | Aggregate action |
|-------|-------------------|------------------|
| `FileScannedEvent` | (none) | Set `TotalFiles` |
| `FileHashingEvent` | Add entry, `State = Hashing` | (none) |
| `FileHashedEvent` | Set `ContentHash`, populate reverse map | Increment `FilesHashed` |
| `TarEntryAddedEvent` | `State → QueuedInTar` | (none) |
| `TarBundleSealingEvent` | All matching hashes: `State → UploadingTar`, set `TarId` | (none) |
| `ChunkUploadingEvent` | If not in tar path: `State → Uploading` | (none) |
| `ChunkUploadedEvent` | Remove entry (large file) | Increment `ChunksUploaded`, add `BytesUploaded` |
| `TarBundleUploadedEvent` | Remove all entries with matching `TarId` | Increment `TarsUploaded`, increment `ChunksUploaded` |
| `SnapshotCreatedEvent` | (none) | Set `SnapshotComplete` |

#### Scenario: TarBundleSealingEvent transitions multiple files
- **WHEN** `TarBundleSealingEvent(5, 100KB, ["hash1", "hash2", "hash3", "hash4", "hash5"])` is published
- **THEN** the handler SHALL look up all 5 content hashes in the reverse map, find their `TrackedFile` entries, and set `State = UploadingTar` and `TarId` on each

#### Scenario: TarBundleUploadedEvent removes multiple files
- **WHEN** `TarBundleUploadedEvent("tarHash", 80KB, 5)` is published
- **THEN** all `TrackedFile` entries with `TarId == "tarHash"` SHALL be removed from the dictionary

### Requirement: Restore notification handlers
The system SHALL implement `INotificationHandler<T>` for all 4 restore notification events. Each handler SHALL update the corresponding fields on `ProgressState`:

| Handler | Action |
|---------|--------|
| `RestoreStartedHandler` | Call `state.SetRestoreTotalFiles(count)` |
| `FileRestoredHandler` | Call `state.IncrementFilesRestored(fileSize)`, `state.AddRestoreEvent(path, size, skipped: false)` |
| `FileSkippedHandler` | Call `state.IncrementFilesSkipped(fileSize)`, `state.AddRestoreEvent(path, size, skipped: true)` |
| `RehydrationStartedHandler` | Call `state.SetRehydration(chunkCount, totalBytes)` |

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
