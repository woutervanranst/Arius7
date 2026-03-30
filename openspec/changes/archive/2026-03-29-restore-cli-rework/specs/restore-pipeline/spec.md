## MODIFIED Requirements

### Requirement: Restore conflict check
The system SHALL check each target file against the local filesystem before restoring, using a `[disposition]` log scope (renamed from `[conflict]`). Four dispositions SHALL be recognized:

1. **New** — file does not exist locally. The system SHALL proceed with restore and log `[disposition] {RelativePath} -> new`.
2. **SkipIdentical** — file exists and its hash matches the snapshot entry. The system SHALL skip the file and log `[disposition] {RelativePath} -> skip (identical)`.
3. **Overwrite** — file exists with a different hash AND `--overwrite` is set. The system SHALL proceed with restore and log `[disposition] {RelativePath} -> overwrite`.
4. **KeepLocalDiffers** — file exists with a different hash AND `--overwrite` is NOT set. The system SHALL NOT restore the file, SHALL log `[disposition] {RelativePath} -> keep (local differs, no --overwrite)`, and SHALL `continue` to the next file (not add it to `toRestore`).

Every disposition SHALL publish a `FileDispositionEvent(RelativePath, Disposition, FileSize)` via the mediator AND log the decision at Information level with the `[disposition]` scope.

#### Scenario: Local file matches snapshot
- **WHEN** local file `photos/vacation.jpg` exists and its hash matches the snapshot entry
- **THEN** the system SHALL skip the file, log `[disposition] photos/vacation.jpg -> skip (identical)`, and publish `FileDispositionEvent` with `Disposition = SkipIdentical`

#### Scenario: Local file differs, no overwrite
- **WHEN** local file `photos/vacation.jpg` exists with a different hash and `--overwrite` is NOT set
- **THEN** the system SHALL NOT restore the file, SHALL log `[disposition] photos/vacation.jpg -> keep (local differs, no --overwrite)`, SHALL publish `FileDispositionEvent` with `Disposition = KeepLocalDiffers`, and SHALL NOT add the file to `toRestore`

#### Scenario: Local file differs, overwrite set
- **WHEN** local file `photos/vacation.jpg` exists with a different hash and `--overwrite` IS set
- **THEN** the system SHALL proceed with restore, log `[disposition] photos/vacation.jpg -> overwrite`, and publish `FileDispositionEvent` with `Disposition = Overwrite`

#### Scenario: Local file does not exist
- **WHEN** the target path has no local file
- **THEN** the system SHALL proceed with restore, log `[disposition] photos/vacation.jpg -> new`, and publish `FileDispositionEvent` with `Disposition = New`

### Requirement: Idempotent restore
Restore SHALL be fully idempotent. Re-running the same restore command SHALL: skip files already restored correctly (hash match), download newly rehydrated chunks, skip still-pending rehydrations (must not issue duplicate requests), and report remaining files. Each run is a self-contained scan-and-act cycle with no persistent local state. The restore pipeline SHALL publish notification events throughout:

- `RestoreStartedEvent(TotalFiles)` before beginning downloads
- `FileRestoredEvent(RelativePath, FileSize)` after each file is written to disk
- `FileSkippedEvent(RelativePath, FileSize)` for each file skipped due to hash match
- `RehydrationStartedEvent(ChunkCount, TotalBytes)` when rehydration is kicked off
- `SnapshotResolvedEvent(Timestamp, RootHash, FileCount)` after snapshot resolution
- `TreeTraversalCompleteEvent(FileCount, TotalOriginalSize)` after tree walk
- `FileDispositionEvent(RelativePath, Disposition, FileSize)` for each file's disposition decision
- `ChunkResolutionCompleteEvent(ChunkGroups, LargeCount, TarCount)` after chunk index lookup
- `RehydrationStatusEvent(Available, Rehydrated, NeedsRehydration, Pending)` after rehydration check
- `ChunkDownloadStartedEvent(ChunkHash, Type, FileCount, CompressedSize)` when chunk download begins
- `ChunkDownloadCompletedEvent(ChunkHash, FilesRestored, CompressedSize)` after each chunk download completes
- `CleanupCompleteEvent(ChunksDeleted, BytesFreed)` after cleanup
- `TreeTraversalProgressEvent(FilesFound)` periodically during tree traversal

Stage-level and aggregate `_mediator.Publish()` calls (e.g., `SnapshotResolvedEvent`, `TreeTraversalCompleteEvent`, `ChunkResolutionCompleteEvent`, `RehydrationStatusEvent`, `ChunkDownloadStartedEvent`, `CleanupCompleteEvent`) SHALL be accompanied by a corresponding `_logger.LogInformation()` call at the same site, mirroring the archive pipeline pattern. High-volume per-item events (`FileRestoredEvent`, `FileSkippedEvent`, `TreeTraversalProgressEvent`) are exempt from `LogInformation` pairing; these MAY use `LogDebug` instead to avoid log spam.

`FileRestoredEvent` and `FileSkippedEvent` SHALL carry `long FileSize` (the file's uncompressed size in bytes) so the CLI can accumulate bytes-restored/skipped and show per-file sizes in the restore tail display.

#### Scenario: Partial restore re-run
- **WHEN** a restore previously restored 500 of 1000 files and rehydration has completed for 300 more chunks
- **THEN** re-running SHALL skip the 500 completed files, restore the 300 newly available, and report 200 still pending

#### Scenario: Full restore complete
- **WHEN** all files have been restored across multiple runs
- **THEN** the system SHALL report all files restored and prompt to clean up `chunks-rehydrated/`

#### Scenario: Progress events emitted during restore
- **WHEN** a restore operation begins
- **THEN** the system SHALL publish `RestoreStartedEvent(TotalFiles)` before downloading, and `FileRestoredEvent(path, size)` / `FileSkippedEvent(path, size)` for each file processed

#### Scenario: Snapshot resolution event
- **WHEN** the snapshot is resolved and the root hash is obtained
- **THEN** the system SHALL publish `SnapshotResolvedEvent` with the snapshot timestamp, root hash, and file count from the tree, and log at Information level with `[snapshot]` scope

#### Scenario: Tree traversal event
- **WHEN** tree traversal completes and all file entries are collected
- **THEN** the system SHALL publish `TreeTraversalCompleteEvent` with total file count and total original size, and log at Information level with `[tree]` scope

#### Scenario: Chunk resolution event
- **WHEN** chunk index lookups complete for all files to restore
- **THEN** the system SHALL publish `ChunkResolutionCompleteEvent` with the number of chunk groups, large file count, and tar count, and log at Information level with `[chunk]` scope

#### Scenario: Rehydration status event
- **WHEN** rehydration availability check completes
- **THEN** the system SHALL publish `RehydrationStatusEvent` with counts of available, already-rehydrated, needs-rehydration, and pending chunks, and log at Information level with `[rehydration]` scope

#### Scenario: Chunk download start event
- **WHEN** a chunk download begins
- **THEN** the system SHALL publish `ChunkDownloadStartedEvent` with the chunk hash, type (large/tar), number of files in the chunk, and compressed size, and log at Information level with `[download]` scope

#### Scenario: Cleanup complete event
- **WHEN** rehydrated blob cleanup finishes
- **THEN** the system SHALL publish `CleanupCompleteEvent` with chunks deleted and bytes freed, and log at Information level with `[cleanup]` scope

### Requirement: Pointer file creation during restore
The system SHALL create `.pointer.arius` files alongside each restored file (unless `--no-pointers` is set). The pointer SHALL contain the content hash. File metadata (created, modified dates) SHALL be set from the tree blob entry on BOTH the restored binary file AND the pointer file.

#### Scenario: Restored file gets pointer with timestamps
- **WHEN** `photos/vacation.jpg` is restored from a snapshot with Created=2025-06-15T10:00:00Z and Modified=2025-06-20T14:30:00Z
- **THEN** `photos/vacation.jpg.pointer.arius` SHALL be created with the content hash
- **AND** the pointer file's CreationTimeUtc SHALL be set to 2025-06-15T10:00:00Z
- **AND** the pointer file's LastWriteTimeUtc SHALL be set to 2025-06-20T14:30:00Z

#### Scenario: Pointer timestamps match binary timestamps
- **WHEN** a binary file is restored and its timestamps are set from the tree entry
- **THEN** the corresponding `.pointer.arius` file SHALL have identical CreationTimeUtc and LastWriteTimeUtc values

## ADDED Requirements

### Requirement: Restore event types
The restore pipeline SHALL define the following additional event types in `RestoreModels.cs`:

- `SnapshotResolvedEvent(DateTimeOffset Timestamp, string RootHash, int FileCount)` — published after snapshot resolution and tree traversal gives the file count
- `TreeTraversalCompleteEvent(int FileCount, long TotalOriginalSize)` — published after all file entries are collected from the tree
- `TreeTraversalProgressEvent(int FilesFound)` — published periodically during tree traversal with the cumulative count of files discovered
- `FileDispositionEvent(string RelativePath, RestoreDisposition Disposition, long FileSize)` — published for each file's disposition decision
- `ChunkResolutionCompleteEvent(int ChunkGroups, int LargeCount, int TarCount, long TotalOriginalBytes = 0, long TotalCompressedBytes = 0)` — published after chunk index lookups with aggregate byte totals
- `RehydrationStatusEvent(int Available, int Rehydrated, int NeedsRehydration, int Pending)` — published after rehydration check
- `ChunkDownloadStartedEvent(string ChunkHash, string Type, int FileCount, long CompressedSize, long OriginalSize)` — published when a chunk download begins, with both compressed and original sizes
- `ChunkDownloadCompletedEvent(string ChunkHash, int FilesRestored, long CompressedSize)` — published after a chunk has been fully downloaded and extracted
- `CleanupCompleteEvent(int ChunksDeleted, long BytesFreed)` — published after cleanup

The `RestoreDisposition` enum SHALL have values: `New`, `SkipIdentical`, `Overwrite`, `KeepLocalDiffers`.

All events SHALL implement `INotification` from the Mediator library.

#### Scenario: FileDispositionEvent with enum
- **WHEN** a file disposition is determined during restore
- **THEN** a `FileDispositionEvent` SHALL be published with the appropriate `RestoreDisposition` enum value

#### Scenario: All new events are INotification
- **WHEN** any new restore event type is instantiated
- **THEN** it SHALL implement `INotification` and be publishable via `_mediator.Publish()`
