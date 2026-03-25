## Why

Arius.Core defines 12 notification event types and publishes them throughout the archive and restore pipelines, but zero `INotificationHandler<T>` implementations exist anywhere in the codebase. Every `_mediator.Publish()` call fires into the void. The CLI has placeholder Spectre.Console progress code that shows indeterminate spinners instead of real progress. Users have no visibility into what's happening during long-running operations.

## What Changes

- **Wire up notification handlers in CLI**: Implement `INotificationHandler<T>` for all 12 existing notification events (8 archive: `FileScannedEvent`, `FileHashingEvent`, `FileHashedEvent`, `ChunkUploadingEvent`, `ChunkUploadedEvent`, `TarBundleSealingEvent`, `TarBundleUploadedEvent`, `SnapshotCreatedEvent`; 4 restore: `RestoreFileStartedEvent`, `RestoreFileCompletedEvent`, `RehydrationStartedEvent`, `RehydrationCompletedEvent`). Handlers update shared progress state consumed by the Spectre.Console display.
- **Multi-stage progress display for archive**: Replace the current indeterminate spinner with a Spectre.Console `LiveDisplay` or `Progress` showing concurrent stage progress:
  ```
  [Scanning   ] ████████████████████████ 100%  1523 files
  [Hashing    ] ████████████░░░░░░░░░░░  52%   789/1523
  [Uploading  ] ██████░░░░░░░░░░░░░░░░░  23%   45 chunks
  ```
  Scanning starts indeterminate (count unknown) and transitions to determinate once enumeration completes.
- **Per-file progress for large files**: For large file hash and upload operations, report `IProgress<long>` with source bytes processed. The `ProgressStream` wrapper (from `pipeline-streaming` change) provides this for uploads. A similar wrapper is needed for the hash stage. The CLI displays per-file progress lines below the aggregate bars for in-flight large files.
- **Restore progress display**: Wire up restore notification events to show files restored, bytes downloaded, and remaining files pending rehydration.

## Capabilities

### New Capabilities
- `progress-display`: Covers the `INotificationHandler<T>` implementations, the Spectre.Console multi-stage display, per-file progress reporting via `IProgress<long>`, and the shared progress state model between handlers and display.

### Modified Capabilities
- `cli`: Update archive and restore progress display requirements to reflect the new multi-stage display with per-file visibility.
- `archive-pipeline`: Notification events may need additional data (e.g., `FileHashingEvent` should include file size for progress denominator, `FileScannedEvent` should support indeterminate-then-determinate transition).
- `restore-pipeline`: Notification events may need enrichment for progress display.

## Impact

- **Arius.Cli**: New `INotificationHandler<T>` implementations (one per event type or a consolidated handler). Rewrite of `CliBuilder.cs` archive/restore progress sections. New shared progress state class.
- **Arius.Core**: Minor — notification event models may need additional fields (e.g., file size on `FileHashingEvent`). The `ProgressStream` from `pipeline-streaming` change provides `IProgress<long>` for upload progress.
- **Dependencies**: Spectre.Console is already a dependency. Mediator source generator will auto-discover the new `INotificationHandler<T>` implementations.
- **Depends on**: `pipeline-streaming` change (for `ProgressStream` providing `IProgress<long>` on uploads). Can be implemented in parallel but the upload progress portion depends on `ProgressStream` being available.
