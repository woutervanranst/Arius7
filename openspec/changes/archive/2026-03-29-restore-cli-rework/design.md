## Context

The archive CLI was recently redesigned with a rich Spectre.Console Live display using a pure-function `BuildArchiveDisplay(ProgressState)` approach driven by 12 event types from the archive pipeline. The restore CLI was given a basic implementation in that same change — just a 4-line stage header and a rolling tail of recent files — but it lacks the responsiveness and polish of the archive display.

The restore pipeline currently emits only 4 event types (`RestoreStartedEvent`, `FileRestoredEvent`, `FileSkippedEvent`, `RehydrationStartedEvent`), compared to the archive's 12. Most pipeline steps produce only log messages, leaving the CLI unable to show meaningful progress until files start completing. Additionally, a TCS coordination deadlock causes indefinite hangs in a specific code path.

## Goals / Non-Goals

**Goals:**
- Fix the TCS deadlock that causes hangs when rehydrated chunks exist but no new rehydration is needed
- Add events for every restore pipeline step so the CLI can show responsive stage-by-stage progress
- Ensure every event is also accompanied by a structured log message (matching archive's pattern)
- Rework the restore display: spinner on active stage, total size, per-file download progress
- Rename `[conflict]` log scope to `[disposition]` and cover all four cases
- Set timestamps on pointer files to match the original file
- Investigate and fix inconsistent binary file timestamps
- Capture all behavioral fixes in failing tests first (TDD approach)

**Non-Goals:**
- Reworking the rehydration confirmation flow (TCS pattern stays, just fix the deadlock)
- Adding byte-level streaming progress during download (requires `ProgressStream` wiring in download path — complex, can be a follow-up)
- Changing the restore pipeline's sequential download approach to parallel

## Decisions

### Decision 1: New restore event model

The restore pipeline will emit events at every significant step, mirroring the archive pattern where every `_logger.Log*` call has a corresponding `_mediator.Publish` call. Here is the complete event map:

| Pipeline Step | Event | Data | Log Scope | Display Purpose |
|---|---|---|---|---|
| Step 1: Snapshot resolved | `SnapshotResolvedEvent` | Timestamp, RootHash, FileCount (from tree) | `[snapshot]` | Show resolved snapshot info |
| Step 2: Tree traversal complete | `TreeTraversalCompleteEvent` | FileCount, TotalOriginalSize | `[tree]` | Set total files + total size for progress header |
| Step 3: Disposition — new | `FileDispositionEvent` | RelativePath, Disposition=New | `[disposition]` | Tally in summary |
| Step 3: Disposition — skip (identical) | `FileDispositionEvent` | RelativePath, Disposition=SkipIdentical, FileSize | `[disposition]` | Tally + existing FileSkippedEvent |
| Step 3: Disposition — overwrite | `FileDispositionEvent` | RelativePath, Disposition=Overwrite | `[disposition]` | Tally in summary |
| Step 3: Disposition — keep (differs) | `FileDispositionEvent` | RelativePath, Disposition=KeepLocalDiffers | `[disposition]` | Tally in summary |
| Step 3: Disposition complete | (computed from tallies, no separate event) | | | Summary line |
| Step 4: Chunk resolution | `ChunkResolutionCompleteEvent` | ChunkGroups, LargeCount, TarCount | `[chunk]` | Stage header |
| Step 5: Rehydration status | `RehydrationStatusEvent` | Available, Rehydrated, NeedsRehydration, Pending | `[rehydration]` | Stage header |
| Step 7: Download chunk start | `ChunkDownloadStartedEvent` | ChunkHash, Type (large/tar), FileCount, CompressedSize | `[download]` | Per-file row appears |
| Step 7: File restored | `FileRestoredEvent` (existing) | RelativePath, FileSize | `[restore]` | Increment counter |
| Step 8: Rehydration kick-off | `RehydrationStartedEvent` (existing) | ChunkCount, TotalBytes | `[rehydration]` | Rehydrating line |
| Step 9: Cleanup complete | `CleanupCompleteEvent` | ChunksDeleted, BytesFreed | `[cleanup]` | Cleanup stage |

**Rationale**: The archive pipeline publishes an event at every log site and the CLI subscribes to drive the display. We adopt the same pattern for restore. Events are cheap (Mediator in-process publish), and the CLI handlers are thin state updates.

### Decision 2: Disposition enum and unified event

Rather than separate events per disposition, use a single `FileDispositionEvent` with a `Disposition` enum:

```csharp
public enum RestoreDisposition { New, SkipIdentical, Overwrite, KeepLocalDiffers }
public sealed record FileDispositionEvent(string RelativePath, RestoreDisposition Disposition, long FileSize) : INotification;
```

The `FileSkippedEvent` remains for backward compatibility (the progress state already handles it), but `FileDispositionEvent` adds the missing cases. The handler in CLI updates disposition tallies on `ProgressState`.

**Rationale**: A single event type with an enum is cleaner than 4 separate event types. The `[disposition]` log scope uses the enum value in the message: `[disposition] {Path} -> {Disposition}`.

### Decision 3: KeepLocalDiffers is NOT an overwrite

When `!opts.Overwrite` and the file exists with a differing hash, the file is NOT restored. This is correct behavior — the user didn't ask to overwrite. But it must be logged and counted. The file is removed from `toRestore` (currently it falls through to `toRestore.Add()` — this is actually a latent bug: it DOES get restored silently even without `--overwrite`).

Wait — re-reading the code at line 129-154:

```csharp
if (File.Exists(localPath))
{
    if (!opts.Overwrite)
    {
        // Hash local file
        if (localHash == file.ContentHash)
        {
            // skip (identical)
            continue;
        }
        // ← falls through to toRestore.Add(file) — RESTORES the file!
    }
    else
    {
        // overwrite (--overwrite set)
    }
}
```

The user stated: "in case of !opts.Overwrite and the file exists with differing hash, do NOT overwrite. this is expected behavior, just log it." But the current code DOES overwrite — it falls through past the `if (!opts.Overwrite)` block and adds the file to `toRestore`. This means the current behavior is:

- `!overwrite` + identical hash → skip ✓
- `!overwrite` + different hash → **silently restore (overwrite)** ✗

The fix: add a `continue` after logging `KeepLocalDiffers` so the file is NOT added to `toRestore`.

### Decision 4: TCS deadlock fix

The deadlock occurs when:
1. No rehydration is needed (`questionTcs` never fires)
2. But rehydrated chunks exist (`ConfirmCleanup` fires, awaiting `cleanupAnswerTcs`)
3. CLI takes the `else` branch at line 558 (`pipelineTask.IsCompleted` appears true because the live loop exited)
4. But `pipelineTask` is actually blocked on `cleanupAnswerTcs`

**Fix**: After the live display loop exits AND `pipelineTask` is not yet complete, always check `cleanupQuestionTcs` before awaiting `pipelineTask`. This applies in both the "no rehydration" path and the "post-rehydration download" path.

Simplified structure:
```text
// After any live display loop exits:
if (!pipelineTask.IsCompleted && cleanupQuestionTcs.Task.IsCompleted)
{
    // Handle cleanup prompt
}
await pipelineTask;
```

### Decision 5: Restore display layout

The reworked `BuildRestoreDisplay` will show:

```text
  ● Resolved     2026-03-28T14:00:00Z (9 files, 6.91 MB)
  ● Checked      9 new, 0 identical, 0 overwrite, 0 kept
  ◐ Restoring    7/9 files  (4.23 / 6.91 MB)
    Restored:    5  (3.12 MB)
    Skipped:     0  (0 B)
```

Stage progression:
- **Resolved**: `○` while resolving snapshot + traversing tree, `●` when `TreeTraversalCompleteEvent` fires
- **Checked**: `○` during disposition check, `●` when all files are checked (disposition tallies are complete — detected when the first download event or chunk resolution event arrives)
- **Restoring**: `○` during downloads, `●` when all files done. Shows total size from `TreeTraversalCompleteEvent`.

The per-file tail (recent files) remains as-is for now. Per-file download progress bars are a non-goal for this change (requires `ProgressStream` integration in the download path).

### Decision 6: Pointer file timestamps

Both `RestoreLargeFileAsync` and `RestoreTarBundleAsync` already set binary file timestamps. Pointer files will get the same treatment:

```csharp
var pointerPath = localPath + ".pointer.arius";
await File.WriteAllTextAsync(pointerPath, contentHash, cancellationToken);
File.SetCreationTimeUtc(pointerPath,  file.Created.UtcDateTime);
File.SetLastWriteTimeUtc(pointerPath, file.Modified.UtcDateTime);
```

### Decision 7: Investigate inconsistent binary timestamps

The timestamp-setting code exists and looks correct. Possible causes for inconsistency:
- Files restored from tar bundles: `file.Created`/`file.Modified` come from `TreeEntry.Created`/`TreeEntry.Modified` which could be null (fallback to `DateTimeOffset.UtcNow`). If the filetree was written before timestamps were added, old entries would have null timestamps.
- Race condition: another process modifying the file after restore.
- The `File.SetCreationTimeUtc` call on macOS may be a no-op depending on the filesystem.

Investigation tasks: check whether the filetree entries actually have timestamps, and test on target platform.

## Risks / Trade-offs

- **[Risk] Changing disposition behavior (KeepLocalDiffers)** → This changes observable behavior: files that were previously silently overwritten will now be skipped. This is the correct behavior per the user's intent, but it's a behavior change. Mitigated by capturing the expected behavior in tests first.
- **[Risk] Event proliferation** → Adding ~8 new event types increases the surface area. Mitigated by following the exact same pattern as archive (thin handlers, ProgressState updates) and keeping events in RestoreModels.cs.
- **[Risk] macOS timestamp behavior** → `File.SetCreationTimeUtc` may not work on all macOS filesystems. Investigation needed; may need to fall back to `touch`-like approach or accept the limitation.
