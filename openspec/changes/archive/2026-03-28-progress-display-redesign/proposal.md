## Why

The archive progress display shows many files "stuck" at `Hashing 100%` or `Queued in TAR` with no visible activity. Progress bars are only useful during the brief byte-reading phase, and the pipeline's inter-stage delays (dedup lookup, channel backpressure, TAR accumulation) create long periods where the display appears frozen. The user cannot tell what the system is doing.

## What Changes

- **Per-file scanning events**: Replace the batch `FileScannedEvent(long TotalFiles)` with per-file `FileScannedEvent(string RelativePath, long FileSize)` so the scanning counter ticks up live during enumeration. Add `ScanCompleteEvent(long TotalFiles, long TotalBytes)` to mark completion. **BREAKING**: `FileScannedEvent` signature changes.
- **Hashed files disappear from per-file display**: Files transition to a new `Hashed` state (invisible in per-file area) when hashing completes. They reappear only if they enter the large-file upload path. Small files are subsumed into their TAR bundle line.
- **TAR bundle as a display entity**: Introduce `TrackedTar` (CLI-side) with states `Accumulating`, `Sealing`, `Uploading`. Show each TAR as a single line with file count, accumulated size, and a progress bar (accumulation bar against `TarTargetSize`, upload bar against total bytes). Individual small file names no longer shown post-hashing.
- **New `TarBundleStartedEvent()`**: Published when the TAR builder starts a new tar. No parameters -- bundle numbering is a CLI display concern.
- **TAR upload byte-level progress**: Wrap the TAR upload `FileStream` in `ProgressStream` using the existing `CreateUploadProgress` callback, keyed by tar hash.
- **Dedup counter on hashing header**: Show `(N unique)` on the hashing stage header to indicate how many files need uploading vs were deduped.
- **Queue depth visibility**: Expose pipeline channel depths (`filePairChannel`, `largeChannel + sealedTarChannel`) to the CLI via callback delegates on `ArchiveOptions`, displayed as `[N pending]` on stage headers.
- **FileState enum simplified**: Remove `QueuedInTar` and `UploadingTar` from `FileState` (those states are now on `TrackedTar`). Add `Hashed` state (invisible in display, stays in `TrackedFiles` for `ContentHashToPath` lookup until cleaned up by downstream events).

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `progress-display`: Major revision -- per-file state machine simplified (`Hashed` state replaces `QueuedInTar`/`UploadingTar`), new `TrackedTar` entity with its own state machine, scanning counter changes to per-file, dedup counter added, queue depth display added.
- `archive-pipeline`: `FileScannedEvent` changes to per-file, new `ScanCompleteEvent` and `TarBundleStartedEvent` added, TAR upload gains `ProgressStream` wiring.
- `cli`: Display rendering rewritten -- stage headers gain dedup count and queue depth, per-file area filters to active-progress files only, TAR bundle lines replace individual small-file lines.

## Impact

- **Arius.Core**: `FileScannedEvent` signature changes (breaking for any handler). New `ScanCompleteEvent` and `TarBundleStartedEvent` event types. `ArchiveOptions` gains `OnHashQueueReady` and `OnUploadQueueReady` callback properties. `ArchivePipelineHandler` enumeration loop publishes per-file instead of batch. TAR upload stage wraps stream in `ProgressStream`.
- **Arius.Cli**: `ProgressState` gains `FilesScanned`, `ScanComplete`, `FilesUnique`, `TrackedTars`, `HashQueueDepth`, `UploadQueueDepth`. `FileState` enum loses `QueuedInTar`/`UploadingTar`, gains `Hashed`. `TrackedTar` class added. Handlers updated for new event signatures and TAR tracking. `BuildArchiveDisplay` rewritten for new layout. `CreateUploadProgress` callback updated to handle both `TrackedFile` and `TrackedTar`.
- **Arius.Cli.Tests**: Existing progress tests need updating for changed `FileState` enum, new event signatures, and new display layout.
