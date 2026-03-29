## 1. Core Models and Events (RestoreModels.cs)

- [x] 1.1 Add `DownloadKind` enum (`LargeFile`, `TarBundle`) to `RestoreModels.cs`
- [x] 1.2 Add `CreateDownloadProgress` callback property to `RestoreOptions`: `Func<string, long, DownloadKind, IProgress<long>>?` defaulting to null
- [x] 1.3 Add `TreeTraversalProgressEvent(int FilesFound)` notification record
- [x] 1.4 Add `ChunkDownloadCompletedEvent(string ChunkHash, int FilesRestored, long CompressedSize)` notification record
- [x] 1.5 Enrich `ChunkResolutionCompleteEvent` with `TotalOriginalBytes` and `TotalCompressedBytes` fields

## 2. Pipeline: Tree Traversal Progress (RestorePipelineHandler.cs)

- [x] 2.1 Add batched `TreeTraversalProgressEvent` emission during `WalkTreeAsync` (every 10 files or 100ms)
- [x] 2.2 Compute and pass `TotalOriginalBytes` / `TotalCompressedBytes` to `ChunkResolutionCompleteEvent` by summing `ShardEntry.OriginalSize` and `CompressedSize` after chunk index lookup

## 3. Pipeline: Parallel Downloads (RestorePipelineHandler.cs)

- [x] 3.1 Add `DownloadWorkers = 4` constant
- [x] 3.2 Replace sequential `foreach` loop (line 308) with `Parallel.ForEachAsync` bounded at `DownloadWorkers`
- [x] 3.3 Change `filesRestored` to `long` with `Interlocked.Increment` for thread-safe updates
- [x] 3.4 Wrap download stream with `ProgressStream` in `RestoreLargeFileAsync` when `CreateDownloadProgress` is not null (identifier = file RelativePath, kind = LargeFile)
- [x] 3.5 Wrap download stream with `ProgressStream` in `RestoreTarBundleAsync` when `CreateDownloadProgress` is not null (identifier = chunk hash, kind = TarBundle)
- [x] 3.6 Emit `ChunkDownloadCompletedEvent` after tar bundle extraction completes
- [x] 3.7 Pass additional metadata (file count, original size) alongside `CreateDownloadProgress` call for tar bundles so CLI can build display label

## 4. Progress State (ProgressState.cs)

- [x] 4.1 Add `TrackedDownload` class with `Key`, `Kind`, `DisplayName`, `CompressedSize`, `BytesDownloaded` (Interlocked), `OriginalSize`
- [x] 4.2 Add `TrackedDownloads` (`ConcurrentDictionary<string, TrackedDownload>`) to `ProgressState`
- [x] 4.3 Add `RestoreFilesDiscovered` counter (long, Interlocked) to `ProgressState`
- [x] 4.4 Add `RestoreTotalCompressedBytes` field to `ProgressState`
- [x] 4.5 Add `RestoreBytesDownloaded` counter (long, Interlocked) to `ProgressState`

## 5. Progress Handlers (ProgressHandlers.cs)

- [x] 5.1 Add `TreeTraversalProgressHandler` -- sets `RestoreFilesDiscovered`
- [x] 5.2 Add `ChunkDownloadCompletedHandler` -- removes `TrackedDownload`, increments `RestoreBytesDownloaded`
- [x] 5.3 Update `ChunkResolutionCompleteHandler` -- set `RestoreTotalOriginalSize` and `RestoreTotalCompressedBytes` from enriched event
- [x] 5.4 Update `FileRestoredHandler` -- remove corresponding `TrackedDownload` for large files, increment `RestoreBytesDownloaded`

## 6. CLI Wiring (CliBuilder.cs)

- [x] 6.1 Wire `CreateDownloadProgress` factory in restore command setup: create `TrackedDownload` entries, return `IProgress<long>` that updates `BytesDownloaded`
- [x] 6.2 Pass file count and original size metadata for tar bundles to build `"TAR bundle (N files, X)"` display name

## 7. Restore Display (CliBuilder.cs - BuildRestoreDisplay)

- [x] 7.1 Stage 1: Implement `○ Resolving  N files...` → `● Resolved  <ts> (N files)` → append size after chunk resolution
- [x] 7.2 Stage 2: Implement `○ Checking  N new, M identical...` → `● Checked  N new, M identical, O overwrite, P kept`
- [x] 7.3 Stage 3: Add aggregate progress bar to Restoring line with dual byte counters `(X / Y download, Z original)`
- [x] 7.4 Replace completed-file tail with active download table: iterate `TrackedDownloads`, render per-item progress bars using `RenderProgressBar`, `SplitSizePair`, `TruncateAndLeftJustify`
- [x] 7.5 Show file relative path for large files, `TAR bundle (N files, X)` for tar bundles in active download rows

## 8. Tests (ProgressTests.cs)

- [x] 8.1 Test `TrackedDownload` lifecycle: add, update bytes, remove on completion
- [x] 8.2 Test `TreeTraversalProgressHandler` updates `RestoreFilesDiscovered`
- [x] 8.3 Test `ChunkDownloadCompletedHandler` removes tracked download and increments bytes
- [x] 8.4 Test updated `FileRestoredHandler` removes tracked download for large files
- [x] 8.5 Test updated `ChunkResolutionCompleteHandler` sets byte totals
- [x] 8.6 Test `BuildRestoreDisplay` renders Resolving phase correctly
- [x] 8.7 Test `BuildRestoreDisplay` renders Checking phase correctly
- [x] 8.8 Test `BuildRestoreDisplay` renders active download table with progress bars
- [x] 8.9 Test `BuildRestoreDisplay` renders aggregate progress bar with dual byte counters
- [x] 8.10 Test `BuildRestoreDisplay` renders completion state correctly
- [x] 8.11 Test thread safety: concurrent `TrackedDownload` add/update/remove from 4 workers
