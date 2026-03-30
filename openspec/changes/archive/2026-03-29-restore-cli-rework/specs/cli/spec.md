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

**Stage headers** (three stages, always shown):

```
  ● Resolved     2026-03-28T14:00:00.0000000+00:00 (9,224 files, 5.16 GB)
  ● Checked      4,613 new, 4,601 identical, 0 overwrite, 10 kept
  ○ Restoring    4,867/9,224 files  █████░░░░░░░░░░░  32%
                 (1.57 / 4.92 GB download, 5.16 GB original)
```

Stage 1 — **Resolved / Resolving**:
- During tree traversal: `[dim]○[/] Resolving    N files...` where N is `RestoreFilesDiscovered` (`:N0` formatted). If no files discovered yet, no count shown.
- After traversal (`TreeTraversalComplete`): `[green]●[/] Resolved     <timestamp> (N files)` initially without size.
- After chunk resolution sets `RestoreTotalOriginalSize > 0`: `[green]●[/] Resolved     <timestamp> (N files, X)` with humanized total original size appended.
- Timestamp: `SnapshotTimestamp.Value.ToString("o")`, or `"?"` if null. The detail string is `Markup.Escape()`-d.

Stage 2 — **Checked**:
- `[dim]○[/]` when no dispositions yet, `[yellow]○[/]` during disposition checks, `[green]●[/]` when complete (detected when `ChunkGroups > 0` or `done > 0`).
- Shows tallies: `N new, N identical, N overwrite, N kept` (all `:N0` formatted).

Stage 3 — **Restoring** (two-line layout when byte totals are known):
- `[dim]○[/]` initially, `[yellow]○[/]` during downloads (`done > 0` or `RestoreBytesDownloaded > 0`), `[green]●[/]` when all files done.
- Line 1: `{symbol} Restoring    {done:N0}/{total:N0} files  {bar}  {pct}%` — progress bar (16 chars, `RenderProgressBar`) tracking compressed download bytes (`RestoreBytesDownloaded / RestoreTotalCompressedBytes`).
- Line 2 (indented 17 spaces): `[dim]({dlCur} / {dlTot} {dlUnit} download, {origStr} original)[/]` — dual byte counters via `SplitSizePair` for download and `Bytes().Humanize()` for original.
- When `RestoreTotalCompressedBytes` is 0 (no byte totals yet), only a single line is shown: `{symbol} Restoring    {done:N0} files` (or `{done:N0}/{total:N0} files` if total is known), with no progress bar or byte counters.

**Active download table** (shown when not all done AND `TrackedDownloads` is non-empty):
- Blank separator line, then a borderless Spectre `Table` (`NoBorder()`, `HideHeaders()`, `NoWrap()` columns) with 4 columns: name | bar | % | size.
- Row data is collected first to compute max widths for padding (same pattern as archive per-file display).
- Name: `TruncateAndLeftJustify(dl.DisplayName, 35)` then `Markup.Escape()`, rendered `[dim]`.
- Bar: `RenderProgressBar(fraction, 12)` tracking `BytesDownloaded / CompressedSize`.
- Percentage: right-aligned, `PadLeft(maxPct)`, rendered `[dim]`.
- Size: `SplitSizePair(BytesDownloaded, CompressedSize)` with `PadLeft` alignment, rendered `[dim]`.

**Tail lines** (shown when not all done AND no active downloads):
- The 10 most recent `RestoreFileEvent` entries from `RecentRestoreEvents`.
- Blank separator line, then each entry:
  ```
  {sym} [dim]{path}[/]  ({sizeStr})
  ```
- `[green]●[/]` for restored (`Skipped = false`), `[dim]○[/]` for skipped (`Skipped = true`).
- Path column: `TruncateAndLeftJustify(path, 40)` then `Markup.Escape()`.
- Size: `fileSize.Bytes().Humanize()` in parentheses, `Markup.Escape()`-d.

**On completion** (`FilesRestored + FilesSkipped >= RestoreTotalFiles`): the active download table and tail lines are both omitted; only the three stage headers remain, all with `[green]●[/]`.

#### Scenario: In-progress restore display with resolved and checked stages
- **WHEN** snapshot is resolved with 9 files totaling 6.91 MB, all dispositions checked (9 new), and 5 of 9 files restored
- **THEN** the display SHALL show Resolved as green bullet with snapshot info, Checked as green bullet with `9 new, 0 identical, 0 overwrite, 0 kept`, and Restoring as yellow with `5/9 files`

#### Scenario: Completed restore display
- **WHEN** `FilesRestored + FilesSkipped == RestoreTotalFiles`
- **THEN** the display SHALL show all three stages with `[green]●[/]` and NO tail lines

#### Scenario: Display before any events
- **WHEN** the live display starts but no events have been received yet
- **THEN** the display SHALL show all stages as `[dim]○[/]` with zeroed counts
