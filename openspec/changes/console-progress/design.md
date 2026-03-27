## Context

The console-progress change was implemented using Spectre.Console `Progress` with dynamic sub-tasks for per-file visibility. Testing revealed that this approach is fundamentally broken: `ProgressTask.StopTask()` does not remove tasks from the display (no `RemoveTask()` API exists), so completed per-file sub-lines accumulate and clutter the output. The nested stage-bar + sub-task hierarchy also pushes the `Progress` widget beyond its design envelope.

This design revision replaces the display approach entirely while keeping the working parts of the implementation (Mediator wiring fix, progress callbacks, TCS restore coordination, responsive poll loop).

### What works (keep as-is)
- Mediator source generator fix (task 1) — CLI handlers are now discovered
- `CreateHashProgress` / `CreateUploadProgress` callbacks on `ArchiveOptions` (task 2) — byte-level progress from Core
- `TarEntryAddedEvent` (task 3) — tar bundling visibility from Core
- `ProgressState` with `ConcurrentDictionary` (task 4) — concurrent tracking foundation
- Notification handlers (task 5) — thin event→state mapping
- Progress callback wiring (task 6) — `IProgress<long>` factory in CLI
- Restore TCS coordination (task 8) — phase-safe console rendering
- Responsive poll loop pattern (Task.WhenAny) — already in place

### What changes
- **Display primitive**: `Spectre.Console.Progress` → `Spectre.Console.Live`
- **Display model**: Stage bars with nested sub-tasks → stage header lines + flat per-file lines
- **ProgressState model**: Separate in-flight dictionaries → unified per-file state machine
- **Core event enrichment**: `TarBundleSealingEvent` gains content hash list

## Goals / Non-Goals

**Goals:**
- Replace `Spectre.Console.Progress` with `Spectre.Console.Live` for archive display — full control over renderable content, true appear/disappear behavior
- Track each file through a unified lifecycle state machine: Hashing → QueuedInTar → UploadingTar → Done (small files) or Hashing → Uploading → Done (large files)
- Show flat per-file progress lines that appear when a file enters the pipeline and disappear when done
- Show persistent stage header lines (Scanning ✓, Hashing N/M, Uploading N/M) above the file lines
- Enrich `TarBundleSealingEvent` with content hashes so the CLI can transition file states
- Keep all existing Core progress infrastructure (callbacks, events) unchanged except `TarBundleSealingEvent`

**Non-Goals:**
- Changing Core's pipeline architecture or control flow
- Modifying the restore display approach (TCS + `Progress` for download phase is fine — it uses a fixed set of tasks, no dynamic add/remove)
- Adding Web UI support (but the design must not preclude it)
- Per-file progress bars in the Live display (use percentage text and optional bar characters rendered as Markup)

## Decisions

### 1. Replace Spectre.Console `Progress` with `Live` for archive display

**Decision**: Use `AnsiConsole.Live(renderable).StartAsync(...)` with `ctx.UpdateTarget(BuildDisplay(state))` on every poll tick. The `BuildDisplay` method reads a snapshot of `ProgressState` and returns a `Rows(...)` containing `Markup` lines — stage headers at the top, per-file lines below. No `ProgressTask` objects involved.

**Rationale**: The `Live` widget is designed for exactly this use case: "update arbitrary content in place without creating new output lines." It supports `UpdateTarget()` to replace the entire renderable each tick, giving full control over what appears and disappears. The `Progress` widget's lack of `RemoveTask()` makes per-file lines impossible.

**Key constraints from Spectre docs**:
- Not thread-safe — all updates must happen within the `StartAsync` callback. Our poll-loop pattern (read `ProgressState`, build renderable, `ctx.UpdateTarget()`) satisfies this since only the poll loop touches the Live context.
- `ctx.Refresh()` is not needed when using `UpdateTarget()` — the target replacement triggers a refresh.
- Overflow: Use `VerticalOverflow.Crop` + `VerticalOverflowCropping.Bottom` so stage headers at the top remain visible when many files are active.

**Alternative considered**: Keeping `Progress` but avoiding dynamic tasks (encoding all file info in a single ProgressTask's description text). Rejected because it loses per-file progress bars and produces an unreadable wall of text in the description field.

### 2. Per-file state machine in ProgressState

**Decision**: Replace the separate `InFlightHashes` and `InFlightUploads` dictionaries with a single `ConcurrentDictionary<string, TrackedFile>` keyed by relative path. `TrackedFile` contains:

```
TrackedFile:
  RelativePath     : string
  ContentHash      : string?          (set after hashing completes)
  State            : FileState enum   (Hashing, QueuedInTar, UploadingTar, Uploading, Done)
  TotalBytes       : long
  BytesProcessed   : long             (Interlocked-updated, for hashing/uploading progress)
  TarId            : string?          (set when assigned to a tar, used for tar upload correlation)
```

```
FileState enum:
  Hashing       — file is being hashed (large and small files)
  QueuedInTar   — file has been hashed and added to a tar bundle awaiting seal/upload
  UploadingTar  — the tar containing this file is being uploaded
  Uploading     — large file chunk is being uploaded directly
  Done          — file processing is complete (line removed from display)
```

State transitions driven by events:

```
FileHashingEvent     → add entry, state = Hashing
FileHashedEvent      → set ContentHash; if small file → TarEntryAddedEvent follows
TarEntryAddedEvent   → state = QueuedInTar
TarBundleSealingEvent → all files with matching ContentHashes → state = UploadingTar, set TarId
ChunkUploadingEvent  → if large file (in Hashing/post-hash) → state = Uploading
ChunkUploadedEvent   → state = Done (remove from display)
TarBundleUploadedEvent → all files with matching TarId → state = Done (remove from display)
```

**Rationale**: A unified state machine per file replaces the fragmented model of separate "in-flight hashes" and "in-flight uploads" dictionaries. The display simply iterates the tracked files and renders based on state. Files in `Done` state are removed from the dictionary.

**Alternative considered**: Keeping separate dictionaries and adding a third one for tar-queued files. Rejected because a file moves through multiple stages and tracking it across three dictionaries requires complex cross-dictionary coordination.

### 3. ContentHash→RelativePath mapping

**Decision**: The `TrackedFile` entry is keyed by `RelativePath` and gains a `ContentHash` field when `FileHashedEvent` fires. A reverse-lookup `ConcurrentDictionary<string, string>` (`ContentHash → RelativePath`) is maintained for events that arrive keyed by content hash (e.g., `TarEntryAddedEvent`, `ChunkUploadingEvent`, `ChunkUploadedEvent`).

**Rationale**: Core events use content hashes as identifiers (because that's the pipeline's natural key after dedup). The display needs file names. The mapping is built naturally from `FileHashedEvent(RelativePath, ContentHash)` which fires before any downstream events for that file.

### 4. Enrich TarBundleSealingEvent with content hashes

**Decision**: Change `TarBundleSealingEvent(int EntryCount, long UncompressedSize)` to `TarBundleSealingEvent(int EntryCount, long UncompressedSize, IReadOnlyList<string> ContentHashes)`. The publish site at `ArchivePipelineHandler` already has `tarEntries` containing the content hashes — just project them into a list.

**Rationale**: The CLI needs to know which files are in the sealed tar so it can transition their state from `QueuedInTar` to `UploadingTar`. Without this, the CLI would need to track which `TarEntryAddedEvent` hashes belong to which tar, and there's no correlation event to tie them together. The enrichment is a legitimate business event — "I sealed a tar containing these content hashes" is meaningful domain information. The data is already available at the publish site.

### 5. Display rendering as pure function

**Decision**: `BuildArchiveDisplay(ProgressState state) → IRenderable` is a pure function that reads a snapshot of `ProgressState` and returns a `Rows(...)` renderable. The structure:

```
Line 1:  ✓ Scanning                              1523 files       (after scanning completes)
Line 2:  ◐ Hashing                               640/1523         (or ✓ when done)
Line 3:  ◐ Uploading                             3/11 chunks      (or ✓ when done)
Line 4:  (blank separator)
Line 5+: video.mp4      ████████░░░░  Hashing     62%  3.1/5.0 GB
Line 6+: data.db        ██████░░░░░░  Hashing     28%  560M/2.0GB
Line 7+: notes.txt                    Queued in TAR
Line 8+: config.yml                   Queued in TAR
Line 9+: readme.md      ████░░░░░░░░  Uploading TAR  33%
...
```

Progress bars are rendered as Markup text (e.g., `[green]████████[/][dim]░░░░[/]`), not Spectre `ProgressTask` objects. File names are truncated/padded to align columns.

**Rationale**: Making the display a pure function of state makes it testable (pass a `ProgressState`, assert the renderable structure), predictable (no hidden mutable Spectre state), and simple (rebuild everything each tick, let the Live widget handle the diff).

### 6. Overflow handling for many active files

**Decision**: Use `VerticalOverflow.Crop` with `VerticalOverflowCropping.Bottom`. This keeps stage headers visible at the top of the display when the number of active file lines exceeds terminal height.

**Rationale**: The most important information is the stage headers (overall progress). Individual file lines are transient. If 100 files are being processed simultaneously and the terminal is 30 rows, the user sees the stage headers + the first ~27 file lines, with the rest cropped. As files complete and disappear, more become visible.

### 7. Restore display rewritten with Live (round 2)

**Decision**: Replace `Spectre.Console.Progress` for both restore download phases (Phase 1 and Phase 3) with `AnsiConsole.Live()` + `BuildRestoreDisplay(ProgressState) → IRenderable`. The `UpdateRestoreTask` helper is deleted. The TCS phase coordination structure is otherwise unchanged.

**Rationale**: Round 1 left the restore display as a single indeterminate-then-determinate progress bar showing only aggregate counts. This gives users no visibility into which files are being restored or skipped. The `Live`-based approach (same pattern as archive) allows showing a stage header with counts and byte totals, plus a rolling tail of the 10 most recent per-file events — all without the dynamic-task-removal limitation that forced the archive rewrite.

**Restore display layout**:
```
  ○ Restoring    650/1000 files
    Restored:    600  (2.3 GB)
    Skipped:     50   (150.0 MB)
    Rehydrating: 7 chunks (1.0 MB)

  ● ...tos/2026/march/IMG_1231.jpg  (1.2 MB)
  ● ...tos/2026/march/IMG_1232.jpg  (3.4 MB)
  ○ ...tos/2026/march/IMG_1233.jpg  (500 KB)   ← skipped (dim)
  ● ...tos/2026/march/IMG_1234.jpg  (2.1 MB)
```

On completion:
```
  ● Restoring    1000/1000 files
    Restored:    600  (2.3 GB)
    Skipped:     400  (1.1 GB)
    Rehydrated:  7 chunks (1.0 MB)
```

Tail lines clear on completion (only stage header shown). The `○` symbol is dim for skipped files; `●` is green for restored. The rehydrating line is shown only when `RehydrationChunkCount > 0`.

### 8. Bundling stage header

**Decision**: Do not show a separate "Bundling" stage header. The per-file lines with `Queued in TAR` and `Uploading TAR` states already communicate bundling activity. The Uploading stage header covers tar uploads.

**Rationale**: The original four-stage display (Scanning, Hashing, Bundling, Uploading) had "Bundling" as a separate progress bar with entry count. In the per-file model, this information is redundant — you can see exactly which files are queued in a tar. The Uploading header's N/M chunks count includes tar uploads. Removing the Bundling header simplifies the display.

### 9. Keep existing decisions that still apply

The following decisions from the previous design revision remain unchanged:
- **Decision 1 (Mediator handler discovery)**: Add source generator references to CLI project.
- **Decision 3 (Progress callbacks on ArchiveOptions)**: `CreateHashProgress` / `CreateUploadProgress` factories.
- **Decision 4 (ProgressStream wiring in hash path)**: Wrap FileStream when callback is provided.
- **Decision 5 (TarEntryAddedEvent)**: Published after each tar entry write.
- **Decision 6 (TCS approach for restore phase coordination)**: TaskCompletionSource pairs for ConfirmRehydration/ConfirmCleanup.
- **Decision 8 (Responsive poll loop with Task.WhenAny)**: Replace unconditional delay.

### 10. Safe Unicode symbols — replace ✓/◐ with ●/○

**Decision**: Replace `✓` (U+2713, CHECK MARK) and `◐` (U+25D0, CIRCLE WITH LEFT HALF BLACK) everywhere in both archive and restore displays with:
- `●` (U+25CF, BLACK CIRCLE) — complete / restored: `[green]●[/]`
- `○` (U+25CB, WHITE CIRCLE) — in-progress: `[yellow]○[/]`
- `○` — not started / dim / skipped: `[dim]○[/]`

**Rationale**: `✓` and `◐` are in the Miscellaneous/Geometric symbols ranges and do not render consistently across terminal fonts (e.g., they show as replacement boxes in many Windows terminals and some Linux mono fonts). `●` and `○` are in the Geometric Shapes block (U+25A0–U+25FF), which has near-universal coverage in monospace fonts (Consolas, DejaVu Sans Mono, Menlo, etc.). The visual distinction (solid vs hollow circle) clearly communicates complete vs in-progress without relying on color alone.

### 11. File name truncation — TruncateAndLeftJustify helper

**Decision**: Introduce `internal static string TruncateAndLeftJustify(string input, int width)` in `CliBuilder`. The rules:
- If `input.Length <= width`: return `input.PadRight(width)`
- If `input.Length > width`: return `"..." + input[^(width - 3)..].PadRight(width)`  *(never shorter than width)*
- After truncation/padding, the caller applies `Markup.Escape()` to the result before embedding in a Markup string.
- Input is the full `RelativePath` (forward-slash, relative to root), **not** `Path.GetFileName`. This preserves path context when file names are similar.

**Rationale**: Using `Path.GetFileName` (round 1 approach) loses directory context. Two files named `photo.jpg` in different subdirectories look identical in the display. Truncating from the left (`"...last-N-chars"`) keeps the most distinctive part of the path (the deepest name) while still fitting the column. The `PadRight` ensures columnar alignment.

### 12. File sizes in archive per-file lines

**Decision**: Each per-file line in `BuildArchiveDisplay` SHALL include a size column formatted with Humanizer (`.Bytes().Humanize()`):

| State | Progress bar | Size column |
|-------|-------------|-------------|
| Hashing | yes | `3.1 GB / 5.0 GB` (BytesProcessed / TotalBytes) |
| Uploading | yes | `1.3 GB / 4.0 GB` |
| QueuedInTar | no | `1.0 KB` (TotalBytes only) |
| UploadingTar | no | `850 B` (TotalBytes only) |

For Hashing/Uploading the size column uses `BytesProcessed.Bytes().Humanize() + " / " + TotalBytes.Bytes().Humanize()`. No progress bar is shown for tar-path states because `CreateUploadProgress` only fires for large-file chunk uploads, not tar bundle uploads; `BytesProcessed` is stale from hashing.

**Rationale**: Byte counts give the user a concrete sense of what is happening (e.g. a 5 GB file hashing vs a 500-byte config file queued in a tar). Humanizer's automatic unit selection (B/KB/MB/GB) keeps the strings short.

### 13. Restore per-file tracking in ProgressState

**Decision**: Add the following to `ProgressState` (round 2):
- `RestoreFileEvent` record: `(string RelativePath, long FileSize, bool Skipped)` — represents one entry in the tail.
- `RecentRestoreEvents`: `ConcurrentQueue<RestoreFileEvent>` capped at 10 entries (dequeue oldest when at capacity before enqueuing new).
- `BytesRestored`: `long` (Interlocked), incremented via `IncrementFilesRestored(long fileSize)`.
- `BytesSkipped`: `long` (Interlocked), incremented via `IncrementFilesSkipped(long fileSize)`.
- `RehydrationTotalBytes`: `long` (Interlocked), set by `SetRehydration(int count, long bytes)`.
- Update `IncrementFilesRestored()` → `IncrementFilesRestored(long fileSize)` — increments `FilesRestored` and `BytesRestored`.
- Update `IncrementFilesSkipped()` → `IncrementFilesSkipped(long fileSize)` — increments `FilesSkipped` and `BytesSkipped`.
- Update `SetRehydrationChunkCount(int)` → `SetRehydration(int count, long bytes)` — sets both `RehydrationChunkCount` and `RehydrationTotalBytes`.
- Add `AddRestoreEvent(string path, long size, bool skipped)` — enqueues to `RecentRestoreEvents`, dequeuing the oldest if count would exceed 10.

**Rationale**: The ring buffer feeds the restore display tail. The byte accumulators allow the header to show total data transferred/skipped. Storing `RehydrationTotalBytes` alongside chunk count completes the rehydration summary line.

## Risks / Trade-offs

- **Custom progress bar rendering** → We render progress bars as Markup strings (`████░░░░`) instead of using Spectre's `ProgressBarColumn`. This means reimplementing bar width calculation and proportional fill. → Mitigation: Simple arithmetic (`filled = width * pct / 100`), and we can extract a helper function. The visual result is equivalent.

- **Full rebuild every 100ms tick** → We rebuild the entire `Rows(...)` renderable every tick instead of mutating existing objects. → Mitigation: The renderable is a few dozen Markup lines at most. Construction is trivially fast (microseconds). Spectre's Live widget handles the terminal diff efficiently.

- **State machine complexity** → A unified per-file state machine is conceptually simpler but has more transition paths than separate dictionaries. → Mitigation: State transitions are driven by events with clear before/after states. Each handler does one transition. Unit tests cover all paths.

- **ContentHash→RelativePath reverse lookup** → Adds a second dictionary to maintain. → Mitigation: Built from a single event (`FileHashedEvent`), never manually cleaned up (entries are harmless after the file completes), and the archive is bounded by the number of files.

- **Live display not thread-safe** → Same constraint as `Progress`. → Mitigation: Same pattern — only the poll loop thread touches the Live context. `ProgressState` is the thread-safe intermediary.
