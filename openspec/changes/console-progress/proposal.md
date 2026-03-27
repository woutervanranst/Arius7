## Why

The console-progress change was implemented but the Spectre.Console `Progress` approach has fundamental limitations:

1. **Dynamic ProgressTask add/remove doesn't work**: `ProgressTask.StopTask()` marks a task as finished but does NOT remove it from the display. There is no `RemoveTask()` API. Completed sub-tasks accumulate visually, making the per-file progress lines unusable.

2. **Nested stage + sub-task display is too complex**: The four-stage display (Scanning, Hashing, Bundling, Uploading) with dynamic per-file sub-lines under Hashing and Uploading pushes Spectre.Console `Progress` beyond what it was designed for. The widget expects a fixed or slowly-growing list of tasks, not rapid add/remove of child tasks.

3. **The conceptual model is wrong**: Users don't think in pipeline stages. They think in files. "What's happening to my files?" is the question the display should answer.

4. **Mediator source generator issue persists**: `Mediator.SourceGenerator` is referenced only in `Arius.Core.csproj`. The CLI handlers are never discovered.

5. **Restore TCS approach is correct but still needed**: The restore phase coordination via `TaskCompletionSource` remains valid and unchanged.

### Previous issues that remain relevant
- Per-file progress not wired (IProgress callbacks still needed)
- Restore thread-safety (TCS approach still the fix)
- Poll loop responsiveness (Task.WhenAny still the fix)

### Previous issues that are addressed differently
- Dynamic ProgressTask lifecycle → replaced by Live display with full rebuild each tick
- Multi-stage aggregate bars with sub-lines → replaced by flat per-file model with stage headers

## What Changes

### Arius.Core (minimal)
- **Enrich `TarBundleSealingEvent`**: Add `IReadOnlyList<string> ContentHashes` to `TarBundleSealingEvent` so the CLI knows which files are in each sealed tar. This is a legitimate business event enrichment — Core is saying "I sealed a tar containing these content hashes."
- **Progress callbacks on ArchiveOptions**: (already implemented) `CreateHashProgress` and `CreateUploadProgress` remain as-is.
- **`TarEntryAddedEvent`**: (already implemented) Remains as-is.

### Arius.Cli (bulk of changes)
- **Replace `Spectre.Console.Progress` with `Spectre.Console.Live`**: Use `AnsiConsole.Live()` with a `Rows` renderable rebuilt every 100ms tick. Full control over what appears and disappears — no dynamic ProgressTask management.
- **Per-file state machine in ProgressState**: Track each file through its lifecycle: `Hashing → QueuedInTar → UploadingTar → Done` (small files) or `Hashing → Uploading → Done` (large files). Files in `Done` state are removed from the display.
- **Stage headers as static summary lines**: Scanning (✓ with count), Hashing (N/M), Uploading (N/M) rendered as `Markup` text lines above the per-file lines. Not `ProgressTask` objects.
- **ContentHash→RelativePath mapping**: Built from `FileHashedEvent` so tar-related events (keyed by content hash) can be displayed with file names.
- **Fix Mediator handler discovery**: (still needed) Add source generator references to CLI project.
- **Restore TCS approach**: (already implemented) Unchanged.

## Capabilities

### Modified Capabilities
- `progress-display`: Major revision — per-file state machine replacing ConcurrentDictionary of in-flight operations, ContentHash→RelativePath mapping, tar file set tracking.
- `cli`: Replace `Spectre.Console.Progress` with `Spectre.Console.Live` for archive display. Build `Rows` renderable each tick. Restore display unchanged.
- `archive-pipeline`: Enrich `TarBundleSealingEvent` with content hash list. Everything else already implemented.
- `restore-pipeline`: No changes.

## Impact

- **Arius.Core**: One record change — `TarBundleSealingEvent` gains `IReadOnlyList<string> ContentHashes`. The publish site must pass the list of hashes. No behavioral changes.
- **Arius.Cli**: `ProgressState` revised to track per-file lifecycle state machine instead of separate in-flight dictionaries. `CliBuilder.cs` archive display rewritten from `Progress` to `Live`. Handler updates for new state model. New `ContentHash→RelativePath` tracking.
- **Arius.Cli.Tests**: ProgressState tests updated for new state model. Display rendering is now testable (build the `Rows` renderable and inspect it, rather than testing Spectre `ProgressTask` interactions).
