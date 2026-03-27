## Why

The console-progress change was implemented but has critical issues discovered during testing:

1. **Mediator source generator doesn't discover CLI handlers**: `Mediator.SourceGenerator` is referenced only in `Arius.Core.csproj`. The 12 `INotificationHandler<T>` implementations in `Arius.Cli/ProgressHandlers.cs` are in a different assembly that the source generator never scans. Every `_mediator.Publish()` fires into the void — progress goes 0-100% instantly on both archive and restore.

2. **Per-file progress not wired**: `ProgressState` tracks a single in-flight hash and a single in-flight upload (last-writer-wins). With 4 hash workers and 4 upload workers running in parallel, the display shows nonsensical data — file name from one thread, bytes from another. The spec requires one sub-line per concurrent operation. Additionally, `SetHashProgress` / `SetUploadProgress` are never called: upload uses a `noOpProgress`, hashing has no `ProgressStream` at all.

3. **Restore cost display thread-safety**: The `ConfirmRehydration` and `ConfirmCleanup` callbacks render Spectre Console tables and interactive prompts from the pipeline thread while `AnsiConsole.Progress()` is live on the CLI thread. Spectre Console is explicitly not thread safe. This causes garbled output observed in testing.

4. **No progress callbacks on ArchiveOptions**: Core's archive pipeline has a `ProgressStream` placeholder with `noOpProgress` for uploads, and no `ProgressStream` at all for hashing. There is no mechanism for the CLI to inject byte-level progress callbacks. Core should expose enough observable data for any UI (console, web) to render progress — `IProgress<long>` factory callbacks on options are the clean pattern (matching `RestoreOptions.ConfirmRehydration`).

5. **No tar bundling visibility**: There is no event for individual files being added to a tar bundle. Users cannot see the current tar filling up. A `TarEntryAddedEvent` is needed.

6. **Upload task has no meaningful denominator**: `uploadTask.MaxValue = 100` is a meaningless placeholder. Total chunk count is not known until dedup completes. The upload bar needs an indeterminate-to-determinate transition.

7. **Poll loop uses unconditional delay**: `await Task.Delay(100)` means the display lags up to 100ms after the pipeline completes. Should use `Task.WhenAny` to respond immediately.

## What Changes

### Arius.Core (minimal, non-UI)
- **Progress callbacks on ArchiveOptions**: Add `Func<string, long, IProgress<long>>? CreateHashProgress` and `Func<string, long, IProgress<long>>? CreateUploadProgress` to `ArchiveOptions`. Wire `ProgressStream` in hash path (wrap `FileStream` before `ComputeHashAsync`) and replace `noOpProgress` in large-file upload path. Add `ProgressStream` to tar upload path.
- **New TarEntryAddedEvent**: New notification record `TarEntryAddedEvent(string ContentHash, int CurrentEntryCount, long CurrentTarSize)` published after each tar entry write. Add corresponding log line for consistency.

### Arius.Cli (bulk of changes)
- **Fix Mediator handler discovery**: Add `Mediator.SourceGenerator` and `Mediator.Abstractions` as direct package references to `Arius.Cli.csproj` so the source generator discovers the 12 handlers.
- **Per-file progress model**: Replace single-file fields in `ProgressState` with `ConcurrentDictionary<string, FileProgress>` for `InFlightHashes` and `InFlightUploads`. `FileProgress` tracks file name, total bytes, and bytes processed (Interlocked-updated).
- **Archive display rewrite**: Dynamic `ProgressTask` per in-flight file added/removed by the display poll loop. New "Bundling" task showing current tar entry count. Upload task transitions from indeterminate to determinate when chunk count becomes known.
- **Restore TCS approach**: Use `TaskCompletionSource` pairs for `ConfirmRehydration` and `ConfirmCleanup` to decouple callbacks from display. No live progress during plan phase (steps 1-6). Progress display only during download phase. Callbacks render freely on a clean console.
- **Poll loop fix**: Use `Task.WhenAny(pipeline, Task.Delay(100))` instead of unconditional `Task.Delay(100)`.
- **New TarEntryAddedHandler**: Handles `TarEntryAddedEvent`, updates bundling counter on `ProgressState`.

## Capabilities

### Modified Capabilities
- `progress-display`: Major revision — per-file concurrent tracking via `ConcurrentDictionary`, tar bundling visibility, TCS-based restore phase coordination, dynamic `ProgressTask` management.
- `cli`: Archive display with dynamic per-file sub-lines, restore display with TCS phase boundaries, poll loop improvement.
- `archive-pipeline`: Progress callbacks on `ArchiveOptions`, `ProgressStream` wiring for hash and upload paths, new `TarEntryAddedEvent`.
- `restore-pipeline`: No pipeline changes. TCS coordination is purely in CLI callback wiring.

## Impact

- **Arius.Core**: Two new optional properties on `ArchiveOptions`. `ProgressStream` wrapping in hash path + tar upload path. One new event record + one `Publish` call + one log line. No behavioral changes — callbacks are optional, default to null/no-op.
- **Arius.Cli**: `Arius.Cli.csproj` gains Mediator package references. `ProgressState` substantially rewritten (single-file fields → concurrent dictionaries). `CliBuilder.cs` archive display rewritten with dynamic tasks. Restore display rewritten with TCS phase coordination. New `TarEntryAddedHandler`.
- **Arius.Cli.Tests**: Existing `ProgressState` tests need updating for new data model. Handler tests need updating. Integration test may need adjustment for Mediator wiring fix. New tests for TCS restore coordination.
