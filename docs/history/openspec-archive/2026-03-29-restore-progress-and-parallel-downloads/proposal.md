## Why

The restore CLI gives no indication of progress during its two longest phases: tree traversal (which downloads many tree blobs silently) and file downloads (which processes chunks one at a time with no byte-level feedback). The display stays at `○ Resolved` for a long time, then shows only completed files in a tail list. Meanwhile, archive has rich per-file progress bars, aggregate counters, and percentage indicators. Additionally, restore downloads chunks sequentially -- a significant performance bottleneck compared to archive's parallel upload pipeline (4 concurrent workers via `Parallel.ForEachAsync`).

## What Changes

- **Parallel restore downloads**: Replace the sequential `foreach` loop over chunks in `RestorePipelineHandler` (line 308) with `Parallel.ForEachAsync` bounded at 4 concurrent workers, matching archive's pattern. Thread-safe counter updates via `Interlocked`.
- **Tree traversal progress signal**: Emit a new event during `WalkTreeAsync` as files are discovered, so the CLI can show `○ Resolving  523 files...` instead of a silent `○ Resolved` for the entire traversal.
- **Checking phase progress**: Show `○ Checking` with a ticking counter while conflict checks run, transitioning to `● Checked` when done (matching the Resolving/Resolved pattern).
- **Aggregate progress bar on Restoring line**: Show overall download progress with bar, percentage, and dual byte counters: `(3.17 / 8.31 GB download, 14.2 GB original)`.
- **Per-download progress bars**: Show active downloads (not completed files) with byte-level progress bars, replacing the completed-file tail list. Large files show the file name; tar bundles show `TAR bundle (N files, X)`.
- **Download progress plumbing**: Add `CreateDownloadProgress` callback factory to `RestoreOptions` (mirroring archive's `CreateUploadProgress`). Wrap download streams with `ProgressStream` in both `RestoreLargeFileAsync` and `RestoreTarBundleAsync`.
- **Populate total sizes after chunk resolution**: Enrich `ChunkResolutionCompleteEvent` with aggregate byte totals (`TotalOriginalBytes`, `TotalCompressedBytes`). Update the Resolved line with file sizes once chunk index lookups provide `OriginalSize`/`CompressedSize`.
- **Tracked downloads in ProgressState**: Add a `TrackedDownload` entity (analogous to archive's `TrackedFile`) with state, byte counters, and metadata for display. Support both large file and tar bundle variants.

## Capabilities

### New Capabilities

_(none -- all changes modify existing capabilities)_

### Modified Capabilities

- `restore-pipeline`: Add parallel download execution (replacing sequential foreach), add `CreateDownloadProgress` callback to `RestoreOptions`, wrap download streams with `ProgressStream`, emit tree traversal progress events, enrich `ChunkResolutionCompleteEvent` with byte totals.
- `progress-display`: Add `TrackedDownload` state machine for restore downloads, add resolving/checking phase counters, add aggregate restore progress bar, replace completed-file tail with active-download display, handle new/enriched restore events.

## Impact

- **Arius.Core**: `RestorePipelineHandler.cs` (parallel downloads, `ProgressStream` wrapping, new events), `RestoreModels.cs` (new events, `RestoreOptions` callback), existing `ProgressStream` reused as-is.
- **Arius.Cli**: `ProgressState.cs` (new `TrackedDownload`, new counters), `ProgressHandlers.cs` (new/updated handlers), `CliBuilder.cs` (`BuildRestoreDisplay` rewrite, `CreateDownloadProgress` factory wiring, restore command setup).
- **Arius.Cli.Tests**: `ProgressTests.cs` (new tests for `TrackedDownload`, parallel event handling, updated display assertions).
- **Thread safety**: `filesRestored` counter must switch to `Interlocked.Increment`. Mediator publish from parallel workers is already proven safe (archive does this). `ProgressState` already uses `ConcurrentDictionary` and `Interlocked` patterns.
- **No breaking changes**: `RestoreOptions` additions are optional callback properties (defaulting to null). Existing callers unaffected.
