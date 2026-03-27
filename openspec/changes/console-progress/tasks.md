## 1. Fix Mediator Handler Discovery

- [x] 1.1 Add `Mediator.SourceGenerator` and `Mediator.Abstractions` package references to `Arius.Cli.csproj`
- [x] 1.2 Verify `AddMediator()` registers handlers from both Core and CLI assemblies (may need to call from CLI startup or pass assembly references)
- [x] 1.3 Write integration test: publish a notification event and verify the CLI handler is invoked

## 2. Core: Progress Callbacks on ArchiveOptions

- [x] 2.1 Add `Func<string, long, IProgress<long>>? CreateHashProgress` property to `ArchiveOptions`
- [x] 2.2 Add `Func<string, long, IProgress<long>>? CreateUploadProgress` property to `ArchiveOptions`
- [x] 2.3 Wire `ProgressStream` in hash path: when `CreateHashProgress` is not null, wrap `FileStream` before `ComputeHashAsync`
- [x] 2.4 Wire `CreateUploadProgress` in upload path: replace `new Progress<long>()` no-op with callback result (or keep no-op if null)

## 3. Core: TarEntryAddedEvent

- [x] 3.1 Add `TarEntryAddedEvent(string ContentHash, int CurrentEntryCount, long CurrentTarSize)` notification record to `ArchiveModels.cs`
- [x] 3.2 Publish `TarEntryAddedEvent` after each `tarWriter.WriteEntryAsync` in tar builder
- [x] 3.3 Add `ILogger.LogDebug` line for consistency with existing event logging

## 4. CLI: Rewrite ProgressState with ConcurrentDictionary

- [x] 4.1 Create `FileProgress` class with `FileName`, `TotalBytes`, `BytesProcessed` (Interlocked-updated)
- [x] 4.2 Replace single-file volatile fields with `ConcurrentDictionary<string, FileProgress> InFlightHashes`
- [x] 4.3 Replace single-file volatile fields with `ConcurrentDictionary<string, FileProgress> InFlightUploads`
- [x] 4.4 Add `CurrentTarEntryCount` and `CurrentTarSize` fields (reset on seal)
- [x] 4.5 Add `TotalChunks` field for upload indeterminate-to-determinate transition
- [x] 4.6 Keep existing aggregate counters (`TotalFiles`, `FilesHashed`, `ChunksUploaded`, `BytesUploaded`, etc.)

## 5. CLI: Update Notification Handlers

- [x] 5.1 Update `FileHashingHandler`: add entry to `InFlightHashes` with file name and size
- [x] 5.2 Update `FileHashedHandler`: remove entry from `InFlightHashes`, increment `FilesHashed`
- [x] 5.3 Update `ChunkUploadingHandler`: add entry to `InFlightUploads` with chunk hash and size
- [x] 5.4 Update `ChunkUploadedHandler`: remove entry from `InFlightUploads`, increment `ChunksUploaded`, add bytes
- [x] 5.5 Add `TarEntryAddedHandler`: update `CurrentTarEntryCount` and `CurrentTarSize`
- [x] 5.6 Update `TarBundleSealingHandler`: reset `CurrentTarEntryCount` and `CurrentTarSize` to 0
- [x] 5.7 Verify remaining handlers (`FileScannedHandler`, `TarBundleUploadedHandler`, `SnapshotCreatedHandler`, restore handlers) are correct

## 6. CLI: Wire Progress Callbacks into ArchiveOptions

- [x] 6.1 In archive command setup, set `CreateHashProgress` to a factory that adds to `InFlightHashes` and returns `IProgress<long>` updating `BytesProcessed`
- [x] 6.2 In archive command setup, set `CreateUploadProgress` to a factory that adds to `InFlightUploads` and returns `IProgress<long>` updating `BytesProcessed`

## 7. CLI: Rewrite Archive Progress Display

- [x] 7.1 Create four aggregate `ProgressTask` entries: Scanning, Hashing, Bundling, Uploading
- [x] 7.2 Scanning: indeterminate until `TotalFiles` is known, then determinate
- [x] 7.3 Hashing: show `FilesHashed / TotalFiles`
- [x] 7.4 Bundling: indeterminate, description shows current tar entry count
- [x] 7.5 Uploading: indeterminate until `TotalChunks` is known, then determinate with `ChunksUploaded / TotalChunks`
- [x] 7.6 Dynamic sub-lines: add/remove `ProgressTask` for each entry in `InFlightHashes` snapshot, showing file name, %, bytes
- [x] 7.7 Dynamic sub-lines: add/remove `ProgressTask` for each entry in `InFlightUploads` snapshot, showing chunk hash, %, bytes
- [x] 7.8 Use `AutoRefresh(true)` — no manual refresh calls
- [x] 7.9 Replace `await Task.Delay(100)` with `await Task.WhenAny(pipelineTask, Task.Delay(100))`

## 8. CLI: Rewrite Restore Progress Display with TCS

- [x] 8.1 Create TCS pairs: `(questionTcs, answerTcs)` for `ConfirmRehydration` and `(cleanupQuestionTcs, cleanupAnswerTcs)` for `ConfirmCleanup`
- [x] 8.2 Wire `ConfirmRehydration` callback: set `questionTcs` result, await `answerTcs` for user response
- [x] 8.3 Wire `ConfirmCleanup` callback: set `cleanupQuestionTcs` result, await `cleanupAnswerTcs` for user response
- [x] 8.4 CLI event loop: await `Task.WhenAny(pipelineTask, questionTcs.Task)` — render cost tables and prompt on clean console when question arrives, set `answerTcs`
- [x] 8.5 Download phase: start `AnsiConsole.Progress()` with determinate bar for files restored / total after confirmation returns
- [x] 8.6 Cleanup phase: after progress auto-clears, await `cleanupQuestionTcs`, render cleanup prompt, set `cleanupAnswerTcs`
- [x] 8.7 Handle case where pipeline completes without needing rehydration (skip cost confirmation, go straight to download progress)
- [x] 8.8 Use `await Task.WhenAny(pipelineTask, Task.Delay(100))` in download phase poll loop

## 9. Tests

- [x] 9.1 Unit test `ProgressState`: concurrent add/update/remove on `InFlightHashes` and `InFlightUploads` from multiple threads
- [x] 9.2 Unit test `FileProgress.BytesProcessed` Interlocked updates under contention
- [x] 9.3 Unit test archive notification handlers: verify each handler updates correct `ProgressState` fields (including new `TarEntryAddedHandler`)
- [x] 9.4 Unit test restore TCS coordination: verify phase transitions (question signaled → answer provided → pipeline unblocked)
- [x] 9.5 Integration test: verify Mediator handler discovery — publish event from Core, verify CLI handler invoked
- [x] 9.6 Integration test: archive with `CreateHashProgress` / `CreateUploadProgress` callbacks — verify byte-level progress reported
