## 1. Fix Mediator Handler Discovery

- [ ] 1.1 Add `Mediator.SourceGenerator` and `Mediator.Abstractions` package references to `Arius.Cli.csproj`
- [ ] 1.2 Verify `AddMediator()` registers handlers from both Core and CLI assemblies (may need to call from CLI startup or pass assembly references)
- [ ] 1.3 Write integration test: publish a notification event and verify the CLI handler is invoked

## 2. Core: Progress Callbacks on ArchiveOptions

- [ ] 2.1 Add `Func<string, long, IProgress<long>>? CreateHashProgress` property to `ArchiveOptions`
- [ ] 2.2 Add `Func<string, long, IProgress<long>>? CreateUploadProgress` property to `ArchiveOptions`
- [ ] 2.3 Wire `ProgressStream` in hash path: when `CreateHashProgress` is not null, wrap `FileStream` before `ComputeHashAsync`
- [ ] 2.4 Wire `CreateUploadProgress` in upload path: replace `new Progress<long>()` no-op with callback result (or keep no-op if null)

## 3. Core: TarEntryAddedEvent

- [ ] 3.1 Add `TarEntryAddedEvent(string ContentHash, int CurrentEntryCount, long CurrentTarSize)` notification record to `ArchiveModels.cs`
- [ ] 3.2 Publish `TarEntryAddedEvent` after each `tarWriter.WriteEntryAsync` in tar builder
- [ ] 3.3 Add `ILogger.LogDebug` line for consistency with existing event logging

## 4. CLI: Rewrite ProgressState with ConcurrentDictionary

- [ ] 4.1 Create `FileProgress` class with `FileName`, `TotalBytes`, `BytesProcessed` (Interlocked-updated)
- [ ] 4.2 Replace single-file volatile fields with `ConcurrentDictionary<string, FileProgress> InFlightHashes`
- [ ] 4.3 Replace single-file volatile fields with `ConcurrentDictionary<string, FileProgress> InFlightUploads`
- [ ] 4.4 Add `CurrentTarEntryCount` and `CurrentTarSize` fields (reset on seal)
- [ ] 4.5 Add `TotalChunks` field for upload indeterminate-to-determinate transition
- [ ] 4.6 Keep existing aggregate counters (`TotalFiles`, `FilesHashed`, `ChunksUploaded`, `BytesUploaded`, etc.)

## 5. CLI: Update Notification Handlers

- [ ] 5.1 Update `FileHashingHandler`: add entry to `InFlightHashes` with file name and size
- [ ] 5.2 Update `FileHashedHandler`: remove entry from `InFlightHashes`, increment `FilesHashed`
- [ ] 5.3 Update `ChunkUploadingHandler`: add entry to `InFlightUploads` with chunk hash and size
- [ ] 5.4 Update `ChunkUploadedHandler`: remove entry from `InFlightUploads`, increment `ChunksUploaded`, add bytes
- [ ] 5.5 Add `TarEntryAddedHandler`: update `CurrentTarEntryCount` and `CurrentTarSize`
- [ ] 5.6 Update `TarBundleSealingHandler`: reset `CurrentTarEntryCount` and `CurrentTarSize` to 0
- [ ] 5.7 Verify remaining handlers (`FileScannedHandler`, `TarBundleUploadedHandler`, `SnapshotCreatedHandler`, restore handlers) are correct

## 6. CLI: Wire Progress Callbacks into ArchiveOptions

- [ ] 6.1 In archive command setup, set `CreateHashProgress` to a factory that adds to `InFlightHashes` and returns `IProgress<long>` updating `BytesProcessed`
- [ ] 6.2 In archive command setup, set `CreateUploadProgress` to a factory that adds to `InFlightUploads` and returns `IProgress<long>` updating `BytesProcessed`

## 7. CLI: Rewrite Archive Progress Display

- [ ] 7.1 Create four aggregate `ProgressTask` entries: Scanning, Hashing, Bundling, Uploading
- [ ] 7.2 Scanning: indeterminate until `TotalFiles` is known, then determinate
- [ ] 7.3 Hashing: show `FilesHashed / TotalFiles`
- [ ] 7.4 Bundling: indeterminate, description shows current tar entry count
- [ ] 7.5 Uploading: indeterminate until `TotalChunks` is known, then determinate with `ChunksUploaded / TotalChunks`
- [ ] 7.6 Dynamic sub-lines: add/remove `ProgressTask` for each entry in `InFlightHashes` snapshot, showing file name, %, bytes
- [ ] 7.7 Dynamic sub-lines: add/remove `ProgressTask` for each entry in `InFlightUploads` snapshot, showing chunk hash, %, bytes
- [ ] 7.8 Use `AutoRefresh(true)` — no manual refresh calls
- [ ] 7.9 Replace `await Task.Delay(100)` with `await Task.WhenAny(pipelineTask, Task.Delay(100))`

## 8. CLI: Rewrite Restore Progress Display with TCS

- [ ] 8.1 Create TCS pairs: `(questionTcs, answerTcs)` for `ConfirmRehydration` and `(cleanupQuestionTcs, cleanupAnswerTcs)` for `ConfirmCleanup`
- [ ] 8.2 Wire `ConfirmRehydration` callback: set `questionTcs` result, await `answerTcs` for user response
- [ ] 8.3 Wire `ConfirmCleanup` callback: set `cleanupQuestionTcs` result, await `cleanupAnswerTcs` for user response
- [ ] 8.4 CLI event loop: await `Task.WhenAny(pipelineTask, questionTcs.Task)` — render cost tables and prompt on clean console when question arrives, set `answerTcs`
- [ ] 8.5 Download phase: start `AnsiConsole.Progress()` with determinate bar for files restored / total after confirmation returns
- [ ] 8.6 Cleanup phase: after progress auto-clears, await `cleanupQuestionTcs`, render cleanup prompt, set `cleanupAnswerTcs`
- [ ] 8.7 Handle case where pipeline completes without needing rehydration (skip cost confirmation, go straight to download progress)
- [ ] 8.8 Use `await Task.WhenAny(pipelineTask, Task.Delay(100))` in download phase poll loop

## 9. Tests

- [ ] 9.1 Unit test `ProgressState`: concurrent add/update/remove on `InFlightHashes` and `InFlightUploads` from multiple threads
- [ ] 9.2 Unit test `FileProgress.BytesProcessed` Interlocked updates under contention
- [ ] 9.3 Unit test archive notification handlers: verify each handler updates correct `ProgressState` fields (including new `TarEntryAddedHandler`)
- [ ] 9.4 Unit test restore TCS coordination: verify phase transitions (question signaled → answer provided → pipeline unblocked)
- [ ] 9.5 Integration test: verify Mediator handler discovery — publish event from Core, verify CLI handler invoked
- [ ] 9.6 Integration test: archive with `CreateHashProgress` / `CreateUploadProgress` callbacks — verify byte-level progress reported
