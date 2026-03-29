## 1. New Event Types and Enum

- [x] 1.1 Add `RestoreDisposition` enum (`New`, `SkipIdentical`, `Overwrite`, `KeepLocalDiffers`) to `RestoreModels.cs`
- [x] 1.2 Add `FileDispositionEvent(string RelativePath, RestoreDisposition Disposition, long FileSize) : INotification` to `RestoreModels.cs`
- [x] 1.3 Add `SnapshotResolvedEvent(DateTimeOffset Timestamp, string RootHash, int FileCount) : INotification` to `RestoreModels.cs`
- [x] 1.4 Add `TreeTraversalCompleteEvent(int FileCount, long TotalOriginalSize) : INotification` to `RestoreModels.cs`
- [x] 1.5 Add `ChunkResolutionCompleteEvent(int ChunkGroups, int LargeCount, int TarCount) : INotification` to `RestoreModels.cs`
- [x] 1.6 Add `RehydrationStatusEvent(int Available, int Rehydrated, int NeedsRehydration, int Pending) : INotification` to `RestoreModels.cs`
- [x] 1.7 Add `ChunkDownloadStartedEvent(string ChunkHash, string Type, int FileCount, long CompressedSize) : INotification` to `RestoreModels.cs`
- [x] 1.8 Add `CleanupCompleteEvent(int ChunksDeleted, long BytesFreed) : INotification` to `RestoreModels.cs`

## 2. Disposition Bug Fix (TDD)

- [ ] 2.1 Write test: `KeepLocalDiffers_WhenNoOverwrite_DoesNotRestore` — verify that when `!opts.Overwrite` and file exists with differing hash, the file is NOT added to `toRestore`
- [ ] 2.2 Write test: `KeepLocalDiffers_WhenNoOverwrite_PublishesDispositionEvent` — verify `FileDispositionEvent` with `KeepLocalDiffers` is published
- [ ] 2.3 Fix `RestorePipelineHandler.cs` step 3: add `continue` after the hash-mismatch-no-overwrite case so the file is not added to `toRestore`
- [ ] 2.4 Add `FileDispositionEvent` publish + log for all 4 disposition cases in the conflict check loop
- [ ] 2.5 Rename `[conflict]` log scope to `[disposition]` in `RestorePipelineHandler.cs`

## 3. Pipeline Events and Logging

- [ ] 3.1 Add `SnapshotResolvedEvent` publish + log at `[snapshot]` scope after snapshot resolution
- [ ] 3.2 Add `TreeTraversalCompleteEvent` publish + log at `[tree]` scope after tree traversal
- [ ] 3.3 Add `ChunkResolutionCompleteEvent` publish + log at `[chunk]` scope after chunk index lookups
- [ ] 3.4 Add `RehydrationStatusEvent` publish + log at `[rehydration]` scope after rehydration check
- [ ] 3.5 Add `ChunkDownloadStartedEvent` publish + log at `[download]` scope when each chunk download begins
- [ ] 3.6 Add `CleanupCompleteEvent` publish + log at `[cleanup]` scope after cleanup
- [ ] 3.7 Audit all existing `_logger.Log*` calls in `RestorePipelineHandler.cs` — add missing `_mediator.Publish` counterparts

## 4. Pointer File Timestamps

- [ ] 4.1 Write test: pointer file gets same Created/Modified timestamps as restored binary
- [ ] 4.2 Set `CreationTimeUtc` and `LastWriteTimeUtc` on pointer files in `RestoreLargeFileAsync`
- [ ] 4.3 Set `CreationTimeUtc` and `LastWriteTimeUtc` on pointer files in `RestoreTarBundleAsync`

## 5. ProgressState Extensions

- [ ] 5.1 Add `SnapshotTimestamp`, `SnapshotRootHash`, `TreeTraversalComplete`, `RestoreTotalOriginalSize` fields to `ProgressState`
- [ ] 5.2 Add disposition tally fields (`DispositionNew`, `DispositionSkipIdentical`, `DispositionOverwrite`, `DispositionKeepLocalDiffers`) to `ProgressState`
- [ ] 5.3 Add chunk/rehydration fields (`ChunkGroups`, `LargeChunkCount`, `TarChunkCount`, `ChunksAvailable`, `ChunksRehydrated`, `ChunksNeedingRehydration`, `ChunksPending`) to `ProgressState`

## 6. Notification Handlers

- [ ] 6.1 Add `SnapshotResolvedHandler` in `ProgressHandlers.cs`
- [ ] 6.2 Add `TreeTraversalCompleteHandler` in `ProgressHandlers.cs` — sets `RestoreTotalFiles`, `RestoreTotalOriginalSize`, `TreeTraversalComplete`
- [ ] 6.3 Add `FileDispositionHandler` in `ProgressHandlers.cs` — increments the appropriate disposition tally
- [ ] 6.4 Add `ChunkResolutionCompleteHandler` in `ProgressHandlers.cs`
- [ ] 6.5 Add `RehydrationStatusHandler` in `ProgressHandlers.cs`
- [ ] 6.6 Add `ChunkDownloadStartedHandler` in `ProgressHandlers.cs` (no-op or log for now)
- [ ] 6.7 Add `CleanupCompleteHandler` in `ProgressHandlers.cs` (no-op or log for now)

## 7. TCS Deadlock Fix (TDD)

- [ ] 7.1 Write test: cleanup TCS is handled when no rehydration is needed but `ConfirmCleanup` fires
- [ ] 7.2 Fix `CliBuilder.cs` `BuildRestoreCommand`: after any live loop exits and `pipelineTask` is not complete, check `cleanupQuestionTcs.Task.IsCompleted` and handle cleanup before awaiting `pipelineTask`

## 8. Rework BuildRestoreDisplay

- [ ] 8.1 Rewrite `BuildRestoreDisplay` with 3-stage layout: Resolved (snapshot info + size), Checked (disposition tallies), Restoring (files + bytes progress)
- [ ] 8.2 Add tail lines showing 10 most recent `RestoreFileEvent` entries
- [ ] 8.3 Wire `BuildRestoreDisplay` into the `AnsiConsole.Live()` loop in `BuildRestoreCommand`

## 9. Tests and Verification

- [ ] 9.1 Add `ProgressTests` for new restore handlers (disposition, snapshot, tree, chunk, rehydration)
- [ ] 9.2 Run all tests (`dotnet test`) and fix any failures
- [ ] 9.3 Run build (`dotnet build`) and fix any compilation errors
