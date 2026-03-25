## 1. Progress State Model

- [ ] 1.1 Create `ProgressState` class with thread-safe counters (Interlocked) for all archive and restore fields
- [ ] 1.2 Register `ProgressState` as singleton in DI (in `AddArius` or CLI startup)

## 2. Archive Notification Handlers

- [ ] 2.1 Implement `INotificationHandler<FileScannedEvent>`: set `ProgressState.TotalFiles`
- [ ] 2.2 Implement `INotificationHandler<FileHashingEvent>`: increment `FilesHashing`, set current hash file name
- [ ] 2.3 Implement `INotificationHandler<FileHashedEvent>`: increment `FilesHashed`, decrement `FilesHashing`
- [ ] 2.4 Implement `INotificationHandler<ChunkUploadingEvent>`: increment `ChunksUploading`, set current upload info
- [ ] 2.5 Implement `INotificationHandler<ChunkUploadedEvent>`: increment `ChunksUploaded`, decrement `ChunksUploading`, add bytes
- [ ] 2.6 Implement `INotificationHandler<TarBundleSealingEvent>`: increment `TarsBundled`
- [ ] 2.7 Implement `INotificationHandler<TarBundleUploadedEvent>`: increment `TarsUploaded`
- [ ] 2.8 Implement `INotificationHandler<SnapshotCreatedEvent>`: set snapshot complete flag

## 3. Restore Notification Handlers

- [ ] 3.1 Implement `INotificationHandler<RestoreStartedEvent>`: set `RestoreTotalFiles`
- [ ] 3.2 Implement `INotificationHandler<FileRestoredEvent>`: increment `FilesRestored`
- [ ] 3.3 Implement `INotificationHandler<FileSkippedEvent>`: increment `FilesSkipped`
- [ ] 3.4 Implement `INotificationHandler<RehydrationStartedEvent>`: set rehydration chunk count

## 4. Enrich Notification Events

- [ ] 4.1 Add `FileSize` property to `FileHashingEvent` record for progress bar denominator
- [ ] 4.2 Update `FileHashingEvent` publish site in `ArchivePipelineHandler` to include file size

## 5. Archive Progress Display

- [ ] 5.1 Replace current indeterminate `AnsiConsole.Progress()` in archive path with multi-stage display (Scanning, Hashing, Uploading tasks)
- [ ] 5.2 Implement indeterminate-to-determinate transition for Scanning task when `TotalFiles` becomes available
- [ ] 5.3 Add per-file progress sub-lines for in-flight large file hashing (file name, %, bytes)
- [ ] 5.4 Add per-file progress sub-lines for in-flight large file uploading (file name, %, bytes)
- [ ] 5.5 Wire `IProgress<long>` callbacks from `ProgressStream` to update `ProgressState` current file progress

## 6. Restore Progress Display

- [ ] 6.1 Replace current `AnsiConsole.Status()` spinner in restore path with `Progress` bar (files restored / total)
- [ ] 6.2 Add exit summary line: "N files restored, M files skipped, P files pending rehydration"

## 7. Tests

- [ ] 7.1 Unit test `ProgressState` thread safety: concurrent increments from multiple threads produce correct totals
- [ ] 7.2 Unit test archive notification handlers: verify each handler updates the correct `ProgressState` field
- [ ] 7.3 Integration test: archive run emits all expected notification events (verify handlers are invoked)
