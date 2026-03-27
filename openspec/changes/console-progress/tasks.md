## 1. Core: Enrich TarBundleSealingEvent

- [ ] 1.1 Change `TarBundleSealingEvent` record to include `IReadOnlyList<string> ContentHashes` parameter
- [ ] 1.2 Update the publish site in `ArchivePipelineHandler.SealCurrentTar()` to project `tarEntries` content hashes into the event
- [ ] 1.3 Update existing `TarBundleSealingHandler` in CLI to accept the new parameter (pass-through for now)

## 2. CLI: Rewrite ProgressState with per-file state machine

- [ ] 2.1 Create `FileState` enum: `Hashing`, `QueuedInTar`, `UploadingTar`, `Uploading`, `Done`
- [ ] 2.2 Create `TrackedFile` class with: `RelativePath`, `ContentHash?`, `State`, `TotalBytes`, `BytesProcessed` (Interlocked), `TarId?`
- [ ] 2.3 Replace `InFlightHashes` and `InFlightUploads` ConcurrentDictionaries with single `ConcurrentDictionary<string, TrackedFile> TrackedFiles` keyed by relative path
- [ ] 2.4 Add `ConcurrentDictionary<string, string> ContentHashToPath` reverse lookup map
- [ ] 2.5 Keep existing aggregate counters (`TotalFiles`, `FilesHashed`, `ChunksUploaded`, `TotalChunks`, `BytesUploaded`, `TarsUploaded`, `SnapshotComplete`)
- [ ] 2.6 Add methods for state transitions: `AddFile(path, size)`, `SetFileHashed(path, hash)`, `SetFileQueuedInTar(hash)`, `SetFilesUploadingTar(hashes, tarId)`, `SetFileUploading(hash)`, `RemoveFile(path)`, `RemoveFilesByTarId(tarId)`

## 3. CLI: Update notification handlers for new state model

- [ ] 3.1 `FileHashingHandler`: call `AddFile(relativePath, fileSize)` → adds TrackedFile with State=Hashing
- [ ] 3.2 `FileHashedHandler`: call `SetFileHashed(relativePath, contentHash)` → sets ContentHash, populates reverse map, increments FilesHashed
- [ ] 3.3 `TarEntryAddedHandler`: call `SetFileQueuedInTar(contentHash)` → looks up path via reverse map, sets State=QueuedInTar
- [ ] 3.4 `TarBundleSealingHandler`: call `SetFilesUploadingTar(contentHashes, tarId)` → looks up paths via reverse map, sets State=UploadingTar and TarId on each
- [ ] 3.5 `ChunkUploadingHandler`: call `SetFileUploading(contentHash)` → looks up path via reverse map, sets State=Uploading (only for files not on tar path)
- [ ] 3.6 `ChunkUploadedHandler`: look up path via reverse map, call `RemoveFile(path)` → removes TrackedFile (large file done). Increment ChunksUploaded, add BytesUploaded.
- [ ] 3.7 `TarBundleUploadedHandler`: call `RemoveFilesByTarId(tarHash)` → removes all TrackedFiles with matching TarId. Increment TarsUploaded, ChunksUploaded.
- [ ] 3.8 Verify `FileScannedHandler`, `SnapshotCreatedHandler`, and restore handlers remain correct (aggregate counter updates only)

## 4. CLI: Rewrite archive display with Spectre.Console Live

- [ ] 4.1 Replace `AnsiConsole.Progress().StartAsync(...)` with `AnsiConsole.Live(renderable).StartAsync(...)`
- [ ] 4.2 Configure Live display: `VerticalOverflow.Crop`, `VerticalOverflowCropping.Bottom`, `AutoClear(false)`
- [ ] 4.3 Implement `BuildArchiveDisplay(ProgressState) → IRenderable` as a pure function returning `Rows(...)`
- [ ] 4.4 Render stage headers as Markup lines: Scanning (✓ with count or spinner), Hashing (N/M or ✓), Uploading (N/M or ✓)
- [ ] 4.5 Render per-file lines from `TrackedFiles` snapshot: file name, progress bar (Markup), state label, percentage, byte counts
- [ ] 4.6 Implement progress bar rendering helper: `RenderProgressBar(double fraction, int width) → string` producing `████░░░░` Markup
- [ ] 4.7 File lines: show progress bar for Hashing/Uploading/UploadingTar states; no bar for QueuedInTar; skip Done state
- [ ] 4.8 Poll loop: `while (!pipelineTask.IsCompleted) { ctx.UpdateTarget(BuildArchiveDisplay(state)); await Task.WhenAny(pipelineTask, Task.Delay(100, ct)); }`
- [ ] 4.9 Final update after loop exits: `ctx.UpdateTarget(BuildArchiveDisplay(state))`
- [ ] 4.10 Remove old `UpdateArchiveTasks` method and all `ProgressTask`/`ProgressContext` archive code

## 5. CLI: Wire progress callbacks for new state model

- [ ] 5.1 Update `CreateHashProgress` factory: look up `TrackedFile` by relative path, return `IProgress<long>` that sets `BytesProcessed`
- [ ] 5.2 Update `CreateUploadProgress` factory: look up `TrackedFile` via `ContentHashToPath` reverse map, reset `BytesProcessed`, return `IProgress<long>` that sets `BytesProcessed`

## 6. Tests

- [ ] 6.1 Unit test `TrackedFile` state transitions: Hashing→QueuedInTar→UploadingTar→Done (small file path)
- [ ] 6.2 Unit test `TrackedFile` state transitions: Hashing→Uploading→Done (large file path)
- [ ] 6.3 Unit test `ProgressState.ContentHashToPath` reverse lookup: populated on hash, used for downstream events
- [ ] 6.4 Unit test `ProgressState` concurrent add/transition/remove from multiple threads
- [ ] 6.5 Unit test archive notification handlers: verify each handler updates correct TrackedFile state and aggregate counters
- [ ] 6.6 Unit test `TarBundleSealingHandler`: verify batch state transition for all files in the tar
- [ ] 6.7 Unit test `TarBundleUploadedHandler`: verify batch removal of all files with matching TarId
- [ ] 6.8 Unit test `BuildArchiveDisplay`: pass known ProgressState, verify rendered output contains expected stage headers and file lines
- [ ] 6.9 Unit test `BuildArchiveDisplay`: verify files in Done state are not rendered
- [ ] 6.10 Unit test `RenderProgressBar`: verify bar character fill at various percentages
- [ ] 6.11 Integration test: verify Mediator handler discovery (publish event from Core, verify CLI handler invoked)
- [ ] 6.12 Integration test: archive with `CreateHashProgress` / `CreateUploadProgress` callbacks — verify byte-level progress reported
