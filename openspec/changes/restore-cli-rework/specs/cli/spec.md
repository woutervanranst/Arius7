## MODIFIED Requirements

### Requirement: Restore progress display with Live and TCS phase coordination
The CLI SHALL use `AnsiConsole.Live()` + `BuildRestoreDisplay(ProgressState) → IRenderable` for both restore download phases (Phase 1 and Phase 3). The `AnsiConsole.Progress()` blocks and `UpdateRestoreTask()` helper SHALL be removed. The TCS phase coordination structure (4 phases, two TCS pairs) is otherwise unchanged.

The restore flow SHALL have distinct phases:

1. **Plan phase** (pipeline steps 1-6): No live progress display.
2. **Cost confirmation**: TCS-coordinated rendering of cost tables and selection prompt on clean console.
3. **Download phase** (step 7+): `AnsiConsole.Live()` with `BuildRestoreDisplay` for files restored / total.
4. **Cleanup confirmation**: Live display exits, cleanup prompt rendered on clean console.

**TCS deadlock fix**: After ANY live display loop exits AND `pipelineTask` is not yet complete, the CLI SHALL check whether `cleanupQuestionTcs.Task.IsCompleted` is true. If so, the CLI SHALL handle the cleanup prompt (ask user, set `cleanupAnswerTcs`) before awaiting `pipelineTask`. This check SHALL apply in ALL code paths — both the "rehydration needed" path and the "no rehydration needed" path — to prevent the deadlock where the pipeline awaits `cleanupAnswerTcs` while the CLI awaits `pipelineTask`.

The simplified structure after any live loop:
```
if (!pipelineTask.IsCompleted && cleanupQuestionTcs.Task.IsCompleted)
{
    // Handle cleanup prompt
}
await pipelineTask;
```

#### Scenario: Cost tables render cleanly
- **WHEN** the restore pipeline invokes `ConfirmRehydration`
- **THEN** the cost tables and prompt SHALL render on a clean console without interference from any live display

#### Scenario: Pipeline completes without rehydration needed
- **WHEN** all chunks are available and `ConfirmRehydration` is not invoked
- **THEN** the CLI SHALL show a Live restore display for the download phase directly

#### Scenario: TCS deadlock prevented — no rehydration, cleanup needed
- **WHEN** no rehydration is needed (questionTcs never fires) but the pipeline invokes `ConfirmCleanup`
- **THEN** the CLI SHALL detect that `cleanupQuestionTcs.Task.IsCompleted` is true, handle the cleanup prompt, and set `cleanupAnswerTcs` before awaiting `pipelineTask`
- **AND** the pipeline SHALL NOT hang indefinitely

#### Scenario: TCS deadlock prevented — post-rehydration download, cleanup needed
- **WHEN** the download live loop exits and `pipelineTask` is not yet complete because the pipeline is awaiting `cleanupAnswerTcs`
- **THEN** the CLI SHALL check `cleanupQuestionTcs.Task.IsCompleted` and handle cleanup before awaiting `pipelineTask`

### Requirement: BuildRestoreDisplay pure function
`BuildRestoreDisplay(ProgressState state) → IRenderable` SHALL be a pure function returning a `Rows(...)` renderable with:

**Stage headers**:
```
  ● Resolved     2026-03-28T14:00:00Z (9 files, 6.91 MB)
  ● Checked      9 new, 0 identical, 0 overwrite, 0 kept
  ◐ Restoring    7/9 files  (4.23 / 6.91 MB)
    Restored:    5  (3.12 MB)
    Skipped:     0  (0 B)
```

Stage progression:
- **Resolved**: `[dim]○[/]` initially, `[green]●[/]` when `TreeTraversalCompleteEvent` fires. Shows snapshot timestamp, file count, and total size.
- **Checked**: `[dim]○[/]` initially, `[yellow]○[/]` during disposition checks, `[green]●[/]` when disposition is complete (detected when the first download event or chunk resolution event arrives). Shows tallies: N new, N identical, N overwrite, N kept.
- **Restoring**: `[dim]○[/]` initially, `[yellow]○[/]` during downloads, `[green]●[/]` when all files done. Shows files restored/total and bytes restored/total. Sub-lines show restored count+bytes and skipped count+bytes.

**Tail lines** (the 10 most recent `RestoreFileEvent` entries from `RecentRestoreEvents`):
```
  [green]●[/] ...tos/2026/march/IMG_1231.jpg  (1.2 MB)
  [green]●[/] ...tos/2026/march/IMG_1232.jpg  (3.4 MB)
  [dim]○[/] ...tos/2026/march/IMG_1233.jpg  (500 KB)
  [green]●[/] ...tos/2026/march/IMG_1234.jpg  (2.1 MB)
```

- `[green]●[/]` for restored (`Skipped = false`), `[dim]○[/]` for skipped (`Skipped = true`)
- Path column: `TruncateAndLeftJustify(path, 40)` then `Markup.Escape()`
- Size: `fileSize.Bytes().Humanize()` in parentheses
- On completion (all files done): tail lines are omitted; only the stage headers are shown with `[green]●[/]`

#### Scenario: In-progress restore display with resolved and checked stages
- **WHEN** snapshot is resolved with 9 files totaling 6.91 MB, all dispositions checked (9 new), and 5 of 9 files restored
- **THEN** the display SHALL show Resolved as green bullet with snapshot info, Checked as green bullet with `9 new, 0 identical, 0 overwrite, 0 kept`, and Restoring as yellow with `5/9 files`

#### Scenario: Completed restore display
- **WHEN** `FilesRestored + FilesSkipped == RestoreTotalFiles`
- **THEN** the display SHALL show all three stages with `[green]●[/]` and NO tail lines

#### Scenario: Display before any events
- **WHEN** the live display starts but no events have been received yet
- **THEN** the display SHALL show all stages as `[dim]○[/]` with zeroed counts
