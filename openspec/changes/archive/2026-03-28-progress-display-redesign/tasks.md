## 1. Core Events (Arius.Core)

- [x] 1.1 Change `FileScannedEvent` from `(long TotalFiles)` to `(string RelativePath, long FileSize)`
- [x] 1.2 Add `ScanCompleteEvent(long TotalFiles, long TotalBytes)` record
- [x] 1.3 Add `TarBundleStartedEvent()` parameterless record
- [x] 1.4 Add `OnHashQueueReady` and `OnUploadQueueReady` callback properties to `ArchiveOptions`

## 2. Pipeline Changes (Arius.Core)

- [x] 2.1 Update `ArchivePipelineHandler` enumeration loop: publish `FileScannedEvent` per file, then `ScanCompleteEvent` at end
- [x] 2.2 Publish `TarBundleStartedEvent()` in tar builder when a new tar is initialized
- [x] 2.3 Call `OnHashQueueReady` and `OnUploadQueueReady` callbacks after channels are created
- [x] 2.4 Wrap TAR upload `FileStream` in `ProgressStream` using `CreateUploadProgress(tarHash, uncompressedSize)`

## 3. ProgressState Redesign (Arius.Cli)

- [x] 3.1 Simplify `FileState` enum: remove `QueuedInTar`/`UploadingTar`, add `Hashed`
- [x] 3.2 Remove `TarId` from `TrackedFile`
- [x] 3.3 Change `ContentHashToPath` from `ConcurrentDictionary<string, string>` to `ConcurrentDictionary<string, ConcurrentBag<string>>`
- [x] 3.4 Add `TrackedTar` class and `TarState` enum
- [x] 3.5 Add `TrackedTars` (`ConcurrentDictionary<int, TrackedTar>`) to `ProgressState`
- [x] 3.6 Add new aggregate fields: `FilesScanned`, `BytesScanned`, `ScanComplete`, `TotalBytes`, `FilesUnique`, `HashQueueDepth`, `UploadQueueDepth`

## 4. Notification Handlers (Arius.Cli)

- [x] 4.1 Update `FileScannedHandler`: increment `FilesScanned` + `BytesScanned` (was: set `TotalFiles`)
- [x] 4.2 Add `ScanCompleteHandler`: set `TotalFiles`, `TotalBytes`, `ScanComplete`
- [x] 4.3 Update `FileHashedHandler`: transition `TrackedFile` to `State = Hashed`
- [x] 4.4 Update `TarEntryAddedHandler`: remove `TrackedFile` (was: transition to `QueuedInTar`), update `TrackedTar.FileCount`/`AccumulatedBytes`, increment `FilesUnique`
- [x] 4.5 Update `TarBundleSealingHandler`: transition `TrackedTar` to `Sealing`, set `TarHash`/`TotalBytes` (was: transition files to `UploadingTar`)
- [x] 4.6 Add `TarBundleStartedHandler`: create new `TrackedTar` with next `BundleNumber`, `State = Accumulating`
- [x] 4.7 Update `ChunkUploadingHandler`: dual lookup — large file → `State = Uploading` + `FilesUnique++`; TAR → `TrackedTar.State = Uploading`
- [x] 4.8 Update `TarBundleUploadedHandler`: remove `TrackedTar` (was: remove files by `TarId`)

## 5. Progress Callback Wiring (Arius.Cli)

- [x] 5.1 Update `CreateUploadProgress` to do dual lookup: `TrackedFiles` first, then `TrackedTars` by `TarHash`
- [x] 5.2 Wire `OnHashQueueReady` and `OnUploadQueueReady` in `ArchiveOptions` creation

## 6. Display Rendering (Arius.Cli)

- [x] 6.1 Update scanning header: tick `FilesScanned` live, flip to `●` when `ScanComplete`
- [x] 6.2 Update hashing header: show `(N unique)` suffix and `[N pending]` queue depth
- [x] 6.3 Update uploading header: show `[N pending]` queue depth
- [x] 6.4 Filter per-file area to only `State is Hashing or Uploading` (remove `QueuedInTar`/`UploadingTar` lines)
- [x] 6.5 Add TAR bundle lines section rendering all `TrackedTars` with state-appropriate progress bars

## 7. Tests

- [x] 7.1 Update existing `FileScannedEvent` handler tests for new per-file signature
- [x] 7.2 Add tests for `ScanCompleteHandler`
- [x] 7.3 Add tests for `TarBundleStartedHandler` / `TrackedTar` lifecycle
- [x] 7.4 Update `TarEntryAddedHandler` tests (file removal, `FilesUnique`)
- [x] 7.5 Update `ChunkUploadingHandler` tests (dual lookup, TAR path)
- [x] 7.6 Update display rendering tests for new layout
