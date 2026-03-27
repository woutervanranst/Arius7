## 1. Core: Enrich TarBundleSealingEvent

- [x] 1.1 Change `TarBundleSealingEvent` record to include `IReadOnlyList<string> ContentHashes` parameter
- [x] 1.2 Update the publish site in `ArchivePipelineHandler.SealCurrentTar()` to project `tarEntries` content hashes into the event
- [x] 1.3 Update existing `TarBundleSealingHandler` in CLI to accept the new parameter (pass-through for now)

## 2. CLI: Rewrite ProgressState with per-file state machine

- [x] 2.1 Create `FileState` enum: `Hashing`, `QueuedInTar`, `UploadingTar`, `Uploading`, `Done`
- [x] 2.2 Create `TrackedFile` class with: `RelativePath`, `ContentHash?`, `State`, `TotalBytes`, `BytesProcessed` (Interlocked), `TarId?`
- [x] 2.3 Replace `InFlightHashes` and `InFlightUploads` ConcurrentDictionaries with single `ConcurrentDictionary<string, TrackedFile> TrackedFiles` keyed by relative path
- [x] 2.4 Add `ConcurrentDictionary<string, string> ContentHashToPath` reverse lookup map
- [x] 2.5 Keep existing aggregate counters (`TotalFiles`, `FilesHashed`, `ChunksUploaded`, `TotalChunks`, `BytesUploaded`, `TarsUploaded`, `SnapshotComplete`)
- [x] 2.6 Add methods for state transitions: `AddFile(path, size)`, `SetFileHashed(path, hash)`, `SetFileQueuedInTar(hash)`, `SetFilesUploadingTar(hashes, tarId)`, `SetFileUploading(hash)`, `RemoveFile(path)`, `RemoveFilesByTarId(tarId)`

## 3. CLI: Update notification handlers for new state model

- [x] 3.1 `FileHashingHandler`: call `AddFile(relativePath, fileSize)` → adds TrackedFile with State=Hashing
- [x] 3.2 `FileHashedHandler`: call `SetFileHashed(relativePath, contentHash)` → sets ContentHash, populates reverse map, increments FilesHashed
- [x] 3.3 `TarEntryAddedHandler`: call `SetFileQueuedInTar(contentHash)` → looks up path via reverse map, sets State=QueuedInTar
- [x] 3.4 `TarBundleSealingHandler`: call `SetFilesUploadingTar(contentHashes, tarId)` → looks up paths via reverse map, sets State=UploadingTar and TarId on each
- [x] 3.5 `ChunkUploadingHandler`: call `SetFileUploading(contentHash)` → looks up path via reverse map, sets State=Uploading (only for files not on tar path)
- [x] 3.6 `ChunkUploadedHandler`: look up path via reverse map, call `RemoveFile(path)` → removes TrackedFile (large file done). Increment ChunksUploaded, add BytesUploaded.
- [x] 3.7 `TarBundleUploadedHandler`: call `RemoveFilesByTarId(tarHash)` → removes all TrackedFiles with matching TarId. Increment TarsUploaded, ChunksUploaded.
- [x] 3.8 Verify `FileScannedHandler`, `SnapshotCreatedHandler`, and restore handlers remain correct (aggregate counter updates only)

## 4. CLI: Rewrite archive display with Spectre.Console Live

- [x] 4.1 Replace `AnsiConsole.Progress().StartAsync(...)` with `AnsiConsole.Live(renderable).StartAsync(...)`
- [x] 4.2 Configure Live display: `VerticalOverflow.Crop`, `VerticalOverflowCropping.Bottom`, `AutoClear(false)`
- [x] 4.3 Implement `BuildArchiveDisplay(ProgressState) → IRenderable` as a pure function returning `Rows(...)`
- [x] 4.4 Render stage headers as Markup lines: Scanning (✓ with count or spinner), Hashing (N/M or ✓), Uploading (N/M or ✓)
- [x] 4.5 Render per-file lines from `TrackedFiles` snapshot: file name, progress bar (Markup), state label, percentage, byte counts
- [x] 4.6 Implement progress bar rendering helper: `RenderProgressBar(double fraction, int width) → string` producing `████░░░░` Markup
- [x] 4.7 File lines: show progress bar for Hashing/Uploading/UploadingTar states; no bar for QueuedInTar; skip Done state
- [x] 4.8 Poll loop: `while (!pipelineTask.IsCompleted) { ctx.UpdateTarget(BuildArchiveDisplay(state)); await Task.WhenAny(pipelineTask, Task.Delay(100, ct)); }`
- [x] 4.9 Final update after loop exits: `ctx.UpdateTarget(BuildArchiveDisplay(state))`
- [x] 4.10 Remove old `UpdateArchiveTasks` method and all `ProgressTask`/`ProgressContext` archive code

## 5. CLI: Wire progress callbacks for new state model

- [x] 5.1 Update `CreateHashProgress` factory: look up `TrackedFile` by relative path, return `IProgress<long>` that sets `BytesProcessed`
- [x] 5.2 Update `CreateUploadProgress` factory: look up `TrackedFile` via `ContentHashToPath` reverse map, reset `BytesProcessed`, return `IProgress<long>` that sets `BytesProcessed`

## 6. Tests (round 1)

- [x] 6.1 Unit test `TrackedFile` state transitions: Hashing→QueuedInTar→UploadingTar→Done (small file path)
- [x] 6.2 Unit test `TrackedFile` state transitions: Hashing→Uploading→Done (large file path)
- [x] 6.3 Unit test `ProgressState.ContentHashToPath` reverse lookup: populated on hash, used for downstream events
- [x] 6.4 Unit test `ProgressState` concurrent add/transition/remove from multiple threads
- [x] 6.5 Unit test archive notification handlers: verify each handler updates correct TrackedFile state and aggregate counters
- [x] 6.6 Unit test `TarBundleSealingHandler`: verify batch state transition for all files in the tar
- [x] 6.7 Unit test `TarBundleUploadedHandler`: verify batch removal of all files with matching TarId
- [x] 6.8 Unit test `BuildArchiveDisplay`: pass known ProgressState, verify rendered output contains expected stage headers and file lines
- [x] 6.9 Unit test `BuildArchiveDisplay`: verify files in Done state are not rendered
- [x] 6.10 Unit test `RenderProgressBar`: verify bar character fill at various percentages
- [x] 6.11 Integration test: verify Mediator handler discovery (publish event from Core, verify CLI handler invoked)
- [x] 6.12 Integration test: archive with `CreateHashProgress` / `CreateUploadProgress` callbacks — verify byte-level progress reported

## 7. Core: Enrich restore events with FileSize

- [ ] 7.1 Change `FileRestoredEvent(string RelativePath)` → `FileRestoredEvent(string RelativePath, long FileSize)` in `RestoreModels.cs`
- [ ] 7.2 Change `FileSkippedEvent(string RelativePath)` → `FileSkippedEvent(string RelativePath, long FileSize)` in `RestoreModels.cs`
- [ ] 7.3 Update skip publish site (step 3, conflict check): pass `fs.Length` as `FileSize` — the `FileStream` used for hash comparison is still open at this point
- [ ] 7.4 Update large file restore publish site (`RestoreLargeFileAsync` caller, step 7): pass `indexEntry.OriginalSize` as `FileSize`
- [ ] 7.5 Update tar entry restore publish site (`RestoreTarBundleAsync`): pass `dataBuffer?.Length ?? 0` as `FileSize`

## 8. CLI: ProgressState restore additions

- [ ] 8.1 Add `RestoreFileEvent` internal record: `(string RelativePath, long FileSize, bool Skipped)`
- [ ] 8.2 Add `BytesRestored` (long, Interlocked) and `BytesSkipped` (long, Interlocked) fields
- [ ] 8.3 Add `RehydrationTotalBytes` (long, Interlocked) field
- [ ] 8.4 Add `RecentRestoreEvents` (`ConcurrentQueue<RestoreFileEvent>`) field
- [ ] 8.5 Update `IncrementFilesRestored()` → `IncrementFilesRestored(long fileSize)`: increment `FilesRestored` and add `fileSize` to `BytesRestored`
- [ ] 8.6 Update `IncrementFilesSkipped()` → `IncrementFilesSkipped(long fileSize)`: increment `FilesSkipped` and add `fileSize` to `BytesSkipped`
- [ ] 8.7 Update `SetRehydrationChunkCount(int)` → `SetRehydration(int count, long bytes)`: set both `RehydrationChunkCount` and `RehydrationTotalBytes`
- [ ] 8.8 Add `AddRestoreEvent(string path, long size, bool skipped)`: enqueue to `RecentRestoreEvents`; if count ≥ 10, dequeue oldest first

## 9. CLI: Update restore notification handlers

- [ ] 9.1 Update `FileRestoredHandler`: call `state.IncrementFilesRestored(notification.FileSize)` and `state.AddRestoreEvent(notification.RelativePath, notification.FileSize, skipped: false)`
- [ ] 9.2 Update `FileSkippedHandler`: call `state.IncrementFilesSkipped(notification.FileSize)` and `state.AddRestoreEvent(notification.RelativePath, notification.FileSize, skipped: true)`
- [ ] 9.3 Update `RehydrationStartedHandler`: call `state.SetRehydration(notification.ChunkCount, notification.TotalBytes)`

## 10. CLI: Archive display refinements

- [ ] 10.1 Add `TruncateAndLeftJustify(string input, int width) → string` helper to `CliBuilder`: if `input.Length <= width` return `input.PadRight(width)`; else return `"..." + input[^(width-3)..].PadRight(width)`
- [ ] 10.2 Replace `✓` (U+2713) with `●` (U+25CF) and `◐` (U+25D0) with `○` (U+25CB) in all `BuildArchiveDisplay` stage header Markup strings
- [ ] 10.3 Replace `Path.GetFileName` truncation in per-file lines with `TruncateAndLeftJustify(file.RelativePath, 30)` followed by `Markup.Escape()`
- [ ] 10.4 Add size column to per-file lines: for Hashing/Uploading states show `BytesProcessed.Bytes().Humanize() + " / " + TotalBytes.Bytes().Humanize()`; for QueuedInTar/UploadingTar states show `TotalBytes.Bytes().Humanize()` only

## 11. CLI: Restore display rewrite

- [ ] 11.1 Implement `BuildRestoreDisplay(ProgressState state) → IRenderable`: returns `Rows(...)` with stage header (4 lines) and tail of `RecentRestoreEvents` (up to 10 lines); on completion tail is omitted
- [ ] 11.2 Replace Phase 1 `AnsiConsole.Progress().StartAsync(...)` block (lines ~398–431) with `AnsiConsole.Live(new Markup("")).Overflow(...).AutoClear(false).StartAsync(async ctx => { while (!pipelineTask.IsCompleted && !questionTcs.Task.IsCompleted) { ctx.UpdateTarget(BuildRestoreDisplay(state)); await Task.WhenAny(...); } ctx.UpdateTarget(BuildRestoreDisplay(state)); })`
- [ ] 11.3 Replace Phase 3 `AnsiConsole.Progress().StartAsync(...)` block (lines ~513–536) with the same Live pattern, exiting when `pipelineTask.IsCompleted`
- [ ] 11.4 Delete `UpdateRestoreTask` method

## 12. Tests (round 2)

- [ ] 12.1 Update `BuildArchiveDisplay` tests: verify `●`/`○` symbols (not `✓`/`◐`)
- [ ] 12.2 Update `BuildArchiveDisplay` tests: verify file name column uses full relative path truncation (not just filename)
- [ ] 12.3 Update `BuildArchiveDisplay` tests: verify size column appears for Hashing/Uploading/QueuedInTar/UploadingTar states
- [ ] 12.4 Unit test `TruncateAndLeftJustify`: short path (no truncation + padding), exact-width path, long path (ellipsis prefix), single-char width edge case
- [ ] 12.5 Unit test `ProgressState.AddRestoreEvent` ring buffer: 15 enqueues → exactly 10 entries retained (most recent)
- [ ] 12.6 Unit test `ProgressState.IncrementFilesRestored(fileSize)` / `IncrementFilesSkipped(fileSize)`: counters and byte accumulators updated correctly
- [ ] 12.7 Unit test `ProgressState.SetRehydration(count, bytes)`: both fields set
- [ ] 12.8 Unit test restore handlers (`FileRestoredHandler`, `FileSkippedHandler`, `RehydrationStartedHandler`) with new signatures
- [ ] 12.9 Unit test `BuildRestoreDisplay`: in-progress state shows `○` header, correct counts/bytes, tail lines with `●`/`○`
- [ ] 12.10 Unit test `BuildRestoreDisplay`: completed state (all files done) shows `●` header, no tail lines
- [ ] 12.11 Unit test `BuildRestoreDisplay`: rehydrating line shown only when `RehydrationChunkCount > 0`
- [ ] 12.12 Run full test suite: `cd src/Arius.Cli.Tests && dotnet run`
