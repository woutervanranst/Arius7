## 1. Progress State Model

- [x] 1.1 Create `ProgressState` class with thread-safe counters (Interlocked) for all archive and restore fields
- [x] 1.2 Register `ProgressState` as singleton in DI (in `AddArius` or CLI startup)

## 2. Archive Notification Handlers

- [x] 2.1 Implement `INotificationHandler<FileScannedEvent>`: set `ProgressState.TotalFiles`
- [x] 2.2 Implement `INotificationHandler<FileHashingEvent>`: increment `FilesHashing`, set current hash file name
- [x] 2.3 Implement `INotificationHandler<FileHashedEvent>`: increment `FilesHashed`, decrement `FilesHashing`
- [x] 2.4 Implement `INotificationHandler<ChunkUploadingEvent>`: increment `ChunksUploading`, set current upload info
- [x] 2.5 Implement `INotificationHandler<ChunkUploadedEvent>`: increment `ChunksUploaded`, decrement `ChunksUploading`, add bytes
- [x] 2.6 Implement `INotificationHandler<TarBundleSealingEvent>`: increment `TarsBundled`
- [x] 2.7 Implement `INotificationHandler<TarBundleUploadedEvent>`: increment `TarsUploaded`
- [x] 2.8 Implement `INotificationHandler<SnapshotCreatedEvent>`: set snapshot complete flag

## 3. Restore Notification Handlers

- [x] 3.1 Implement `INotificationHandler<RestoreStartedEvent>`: set `RestoreTotalFiles`
- [x] 3.2 Implement `INotificationHandler<FileRestoredEvent>`: increment `FilesRestored`
- [x] 3.3 Implement `INotificationHandler<FileSkippedEvent>`: increment `FilesSkipped`
- [x] 3.4 Implement `INotificationHandler<RehydrationStartedEvent>`: set rehydration chunk count

## 4. Enrich Notification Events

- [x] 4.1 Add `FileSize` property to `FileHashingEvent` record for progress bar denominator
- [x] 4.2 Update `FileHashingEvent` publish site in `ArchivePipelineHandler` to include file size

## 5. Archive Progress Display

- [x] 5.1 Replace current indeterminate `AnsiConsole.Progress()` in archive path with multi-stage display (Scanning, Hashing, Uploading tasks)
- [x] 5.2 Implement indeterminate-to-determinate transition for Scanning task when `TotalFiles` becomes available
- [x] 5.3 Add per-file progress sub-lines for in-flight large file hashing (file name, %, bytes)
- [x] 5.4 Add per-file progress sub-lines for in-flight large file uploading (file name, %, bytes)
- [x] 5.5 Wire `IProgress<long>` callbacks from `ProgressStream` to update `ProgressState` current file progress

## 6. Restore Progress Display

- [x] 6.1 Replace current `AnsiConsole.Status()` spinner in restore path with `Progress` bar (files restored / total)
- [x] 6.2 Add exit summary line: "N files restored, M files skipped, P files pending rehydration"

## 7. Tests

- [x] 7.1 Unit test `ProgressState` thread safety: concurrent increments from multiple threads produce correct totals
- [x] 7.2 Unit test archive notification handlers: verify each handler updates the correct `ProgressState` field
- [x] 7.3 Integration test: archive run emits all expected notification events (verify handlers are invoked)
