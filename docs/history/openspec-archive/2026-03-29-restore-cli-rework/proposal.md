## Why

The restore CLI has several correctness and UX issues: a TCS deadlock causes indefinite hangs when rehydrated chunks exist but no new rehydration is needed, the `[conflict]` log scope mislabels non-conflict decisions, a silent fourth disposition case goes unlogged, pointer files don't inherit the original file's timestamps, and the progress display is static (no spinner, no total size, no per-file download progress). The archive CLI was recently reworked with a rich Spectre.Console Live display; the restore CLI should be brought to the same standard.

## What Changes

- **Fix TCS deadlock**: The cleanup confirmation TCS is only checked inside the rehydration-question branch. When the pipeline completes without triggering `ConfirmRehydration` but does trigger `ConfirmCleanup`, the CLI awaits `pipelineTask` while the pipeline awaits `cleanupAnswerTcs` — deadlock. Fix by handling cleanup in all code paths. Capture the deadlock in a failing test first (TDD).
- **Rename `[conflict]` log scope to `[disposition]`**: The current scope covers all per-file decisions including `-> new` which is not a conflict. Rename to `[disposition]` and review labels.
- **Log and fix the silent fourth case**: When `!opts.Overwrite` and the file exists with a differing hash, the file is silently overwritten (the code falls through to `toRestore.Add()`). Fix to skip the file and add `[disposition] {Path} -> keep (local differs, no --overwrite)` log. Capture in a test.
- **Add restore progress events and logging**: Mirror the archive pipeline's approach — every significant step emits both a Mediator notification event (for CLI progress display) AND a structured log message. Add events for: snapshot resolved, tree traversal complete, disposition summary, download started/progress/completed per file, rehydration status.
- **Rework restore progress display**: Replace the static `BuildRestoreDisplay` with a richer display that includes a spinner on the active stage, total size alongside file count, and per-file download progress bars (matching archive display patterns).
- **Set pointer file timestamps**: `.pointer.arius` files should inherit the same Created/Modified timestamps as the restored binary file.
- **Investigate inconsistent binary timestamps**: Some restored binaries have correct Created/Modified times and others don't. Investigate and fix the root cause.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `restore-pipeline`: Add the missing disposition log for the "local differs, no --overwrite" case. Ensure every pipeline step emits both a log message and a Mediator notification. Set timestamps on pointer files. Fix timestamp consistency for binary files.
- `cli`: Fix TCS deadlock by handling cleanup confirmation in all restore code paths. Rework restore progress display with spinner, total size, per-file progress.
- `progress-display`: Add new restore-specific progress events and ProgressState fields for richer display (download byte-level progress, disposition summary, snapshot resolution info).

## Impact

- `src/Arius.Core/Restore/RestorePipelineHandler.cs` — disposition logging, pointer timestamps, new events
- `src/Arius.Core/Restore/RestoreModels.cs` — new event types
- `src/Arius.Cli/CliBuilder.cs` — TCS deadlock fix, reworked `BuildRestoreCommand` and `BuildRestoreDisplay`
- `src/Arius.Cli/ProgressHandlers.cs` — new handlers for restore events
- `src/Arius.Cli/ProgressState.cs` — new restore tracking fields
- `src/Arius.Core.Tests/` — new tests for disposition cases, TCS coordination, pointer timestamps
