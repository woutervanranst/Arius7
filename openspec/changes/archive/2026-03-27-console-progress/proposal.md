## Why

The console-progress change was implemented but the Spectre.Console `Progress` approach has fundamental limitations:

1. **Dynamic ProgressTask add/remove doesn't work**: `ProgressTask.StopTask()` marks a task as finished but does NOT remove it from the display. There is no `RemoveTask()` API. Completed sub-tasks accumulate visually, making the per-file progress lines unusable.

2. **Nested stage + sub-task display is too complex**: The four-stage display (Scanning, Hashing, Bundling, Uploading) with dynamic per-file sub-lines under Hashing and Uploading pushes Spectre.Console `Progress` beyond what it was designed for. The widget expects a fixed or slowly-growing list of tasks, not rapid add/remove of child tasks.

3. **The conceptual model is wrong**: Users don't think in pipeline stages. They think in files. "What's happening to my files?" is the question the display should answer.

4. **Mediator source generator issue persists**: `Mediator.SourceGenerator` is referenced only in `Arius.Core.csproj`. The CLI handlers are never discovered.

5. **Restore TCS approach is correct but still needed**: The restore phase coordination via `TaskCompletionSource` remains valid and unchanged.

6. **Restore display uses `Progress` with a fixed bar** — shows only aggregate count/total with no file names, sizes, or per-file tail. After round 1 the display is functional but uninformative.

### Previous issues that remain relevant
- Per-file progress not wired (IProgress callbacks still needed)
- Restore thread-safety (TCS approach still the fix)
- Poll loop responsiveness (Task.WhenAny still the fix)

### Previous issues that are addressed differently
- Dynamic ProgressTask lifecycle → replaced by Live display with full rebuild each tick
- Multi-stage aggregate bars with sub-lines → replaced by flat per-file model with stage headers

## What Changes

### Arius.Core (minimal)
- **Enrich `TarBundleSealingEvent`**: Add `IReadOnlyList<string> ContentHashes` to `TarBundleSealingEvent` so the CLI knows which files are in each sealed tar. *(Round 1: done.)*
- **Progress callbacks on ArchiveOptions**: `CreateHashProgress` and `CreateUploadProgress`. *(Round 1: done.)*
- **`TarEntryAddedEvent`**: Published after each tar entry write. *(Round 1: done.)*
- **Enrich `FileRestoredEvent` and `FileSkippedEvent`**: Add `long FileSize` parameter so the CLI can accumulate bytes-restored/skipped and show per-file sizes in the restore tail. *(Round 2.)*

### Arius.Cli (bulk of changes)
- **Replace `Spectre.Console.Progress` with `Spectre.Console.Live` for archive**: Use `AnsiConsole.Live()` with a `Rows` renderable rebuilt every 100ms tick. *(Round 1: done.)*
- **Per-file state machine in ProgressState**: Track each file through its lifecycle. *(Round 1: done.)*
- **Stage headers as static summary lines**: Scanning/Hashing/Uploading rendered as `Markup` text lines. *(Round 1: done.)*
- **Replace `Spectre.Console.Progress` with `Spectre.Console.Live` for restore**: Same `Live`-based approach as archive. Show stage header with file counts + byte totals, plus a rolling tail of the 10 most recent per-file events. *(Round 2.)*
- **Safe Unicode symbols**: Replace `✓` (U+2713) and `◐` (U+25D0) with `●` (U+25CF, complete) and `○` (U+25CB, in-progress/skipped). These are in the Geometric Shapes block and render reliably in all monospace terminals. *(Round 2.)*
- **File name truncation helper**: `TruncateAndLeftJustify(string input, int width)` — truncates the full relative path (not just filename) to fit the column, prefixing with `"..."` when truncated, then `PadRight(width)`. Used in both archive per-file lines and restore tail lines. *(Round 2.)*
- **File sizes in archive per-file lines**: Show `processed/total` (e.g. `3.1/5.0 GB`) for Hashing/Uploading states, and `total` only (e.g. `1.0 KB`) for QueuedInTar/UploadingTar states. *(Round 2.)*
- **Restore per-file tracking**: Ring buffer of the 10 most recent restore file events (`RestoreFileEvent` record), byte accumulators for restored/skipped, rehydration byte total stored alongside chunk count. *(Round 2.)*

## Capabilities

### Modified Capabilities
- `progress-display`: Major revision — per-file state machine replacing ConcurrentDictionary of in-flight operations, ContentHash→RelativePath mapping, tar file set tracking. Round 2 adds restore per-file tracking and ring buffer.
- `cli`: Replace `Spectre.Console.Progress` with `Spectre.Console.Live` for archive display. Round 2: same for restore display; add `TruncateAndLeftJustify`; update symbols; add file sizes.
- `archive-pipeline`: Enrich `TarBundleSealingEvent` with content hash list. *(done)*
- `restore-pipeline`: Enrich `FileRestoredEvent` and `FileSkippedEvent` with `long FileSize`.

## Impact

- **Arius.Core**: `FileRestoredEvent` and `FileSkippedEvent` gain a `long FileSize` parameter. Publish sites updated to provide size from context (local file, index entry, or buffer).
- **Arius.Cli**: `ProgressState` gains restore byte accumulators, `RehydrationTotalBytes`, and `RecentRestoreEvents` ring buffer. `CliBuilder.cs` restore display rewritten from `Progress` to `Live`; archive display updated for new symbols, truncation, and sizes. `TruncateAndLeftJustify` helper added. `UpdateRestoreTask` removed.
- **Arius.Cli.Tests**: Existing display/handler tests updated; new tests for `BuildRestoreDisplay`, `TruncateAndLeftJustify`, restore event enrichment, ring buffer behavior.
