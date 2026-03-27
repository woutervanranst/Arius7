## Context

The console-progress change was initially implemented but has 7 critical issues discovered during testing. All existing tasks were marked complete but the implementation is broken. This design revision addresses the root causes and documents the corrected approach.

Current state of the codebase:
- `Mediator.SourceGenerator` is only referenced in `Arius.Core.csproj`. The 12 `INotificationHandler<T>` implementations in `Arius.Cli/ProgressHandlers.cs` are never discovered by the source generator because it only scans the assembly it's referenced from. Every `_mediator.Publish()` is a no-op in production.
- `ProgressState` tracks a single `CurrentHashFile` / `CurrentUploadFile` (volatile string, last-writer-wins). With 4 parallel workers per stage, this produces nonsensical display data.
- `SetHashProgress()` / `SetUploadProgress()` exist on `ProgressState` but are never called. The upload path uses `new Progress<long>()` (no-op). The hash path has no `ProgressStream` at all.
- `ConfirmRehydration` and `ConfirmCleanup` callbacks render Spectre Console tables and prompts from the pipeline thread while `AnsiConsole.Progress()` is live on the CLI thread. Spectre Console live components are explicitly not thread-safe, causing garbled output.
- No `TarEntryAddedEvent` exists — users cannot see files being added to tar bundles.
- `uploadTask.MaxValue = 100` is a meaningless placeholder; total chunk count is not known until dedup completes.
- Poll loops use `await Task.Delay(100)` unconditionally, adding up to 100ms lag after pipeline completion.

## Goals / Non-Goals

**Goals:**
- Fix Mediator handler discovery so all 12+ handlers are invoked in production
- Track concurrent per-file progress for parallel hash and upload workers
- Wire `IProgress<long>` callbacks from CLI into Core's pipeline for byte-level progress
- Coordinate restore display with pipeline callbacks via TCS to avoid thread-safety issues
- Add tar bundling visibility via new `TarEntryAddedEvent`
- Make upload progress transition from indeterminate to determinate
- Make poll loops responsive to pipeline completion

**Non-Goals:**
- Changing Core's pipeline architecture or control flow
- Adding Web UI support (but the design must not preclude it — Core exposes observable data, not display logic)
- Logging integration (progress display is separate from structured logging)
- Non-interactive mode (Spectre.Console handles fallback natively)

## Decisions

### 1. Fix Mediator source generator discovery

**Decision**: Add `Mediator.SourceGenerator` and `Mediator.Abstractions` as direct package references to `Arius.Cli.csproj`. The source generator must scan the CLI assembly to discover handlers there. Additionally, `AddMediator()` must be called in a way that registers handlers from both assemblies (Core and CLI).

**Rationale**: The source generator is per-assembly — it only generates registration code for `INotificationHandler<T>` implementations it finds in the assembly where it's referenced. The CLI handlers are invisible to Core's source generator output. This is the root cause of the 0→100% jump.

**Alternative considered**: Moving handlers into Core. Rejected because handlers depend on `ProgressState` which is a CLI concern. Core should not know about display state.

### 2. ConcurrentDictionary for per-file progress

**Decision**: Replace the single `CurrentHashFile` / `CurrentUploadFile` volatile strings in `ProgressState` with:
- `ConcurrentDictionary<string, FileProgress> InFlightHashes` — keyed by relative path
- `ConcurrentDictionary<string, FileProgress> InFlightUploads` — keyed by content hash

`FileProgress` is a class with: `string FileName`, `long TotalBytes`, `long BytesProcessed` (updated via `Interlocked.Exchange`).

Handlers add entries on operation start (`FileHashingEvent` / `ChunkUploadingEvent`) and remove them on completion (`FileHashedEvent` / `ChunkUploadedEvent`). The display loop reads a snapshot of the dictionary each tick to render sub-lines.

**Rationale**: With 4 hash workers and 4 upload workers running in parallel, a single last-writer-wins field is meaningless. A `ConcurrentDictionary` naturally tracks the set of in-flight operations. `Interlocked.Exchange` for byte progress avoids locks while providing correct reads.

**Alternative considered**: Array of fixed slots (one per worker). Rejected because it couples the display to a fixed worker count and requires slot allocation logic.

### 3. Progress callbacks on ArchiveOptions

**Decision**: Add two optional callback properties to `ArchiveOptions`:
```csharp
Func<string, long, IProgress<long>>? CreateHashProgress    // (relativePath, fileSize) → progress
Func<string, long, IProgress<long>>? CreateUploadProgress   // (contentHash, size) → progress
```

Core calls these factories when starting a hash/upload operation. The returned `IProgress<long>` is passed to `ProgressStream`. If `null`, Core falls back to current behavior (no progress reporting for hash, no-op `Progress<long>` for upload).

**Rationale**: This follows the same pattern as `RestoreOptions.ConfirmRehydration` — Core exposes hooks, the UI injects callbacks. Core remains UI-agnostic. The CLI's factory implementation creates an `IProgress<long>` that updates the corresponding `FileProgress.BytesProcessed` entry in `ProgressState`.

**Alternative considered**: Having Core publish byte-level Mediator events. Rejected because Mediator publish per-byte-chunk would add measurable overhead for multi-GB files. `IProgress<long>` is designed for this use case.

### 4. ProgressStream wiring in hash path

**Decision**: In `ArchivePipelineHandler`, when `CreateHashProgress` is not null, wrap the `FileStream` in a `ProgressStream` before passing to `ComputeHashAsync`. The `ProgressStream` reports cumulative bytes to the `IProgress<long>`. When null, hash the raw stream directly (zero overhead).

**Rationale**: `ProgressStream` already exists in `Arius.Core/Streaming/` and is proven in the upload path. Reusing it for hashing is trivial. The hash function reads from a `Stream`, so wrapping is transparent.

### 5. TarEntryAddedEvent

**Decision**: Add a new notification record `TarEntryAddedEvent(string ContentHash, int CurrentEntryCount, long CurrentTarSize)` published after each `tarWriter.WriteEntryAsync` call. Add a corresponding `ILogger.LogDebug` line for consistency with existing event logging. Add a `TarEntryAddedHandler` in CLI that updates `ProgressState.CurrentTarEntryCount` and `CurrentTarSize`. The `TarBundleSealingHandler` resets these to 0.

**Rationale**: Users need visibility into tar bundling — especially when many small files are being packed. Without this, the display has a gap between "file hashed" and "tar uploaded" where nothing visually happens. The event is lightweight (published once per file added to tar, not per byte).

### 6. TCS approach for restore phase coordination

**Decision**: Use `TaskCompletionSource` pairs to coordinate between the restore pipeline's callbacks and the CLI display. The flow:

1. CLI creates two TCS pairs: `(questionTcs, answerTcs)` for `ConfirmRehydration` and `(cleanupQuestionTcs, cleanupAnswerTcs)` for `ConfirmCleanup`.
2. The `ConfirmRehydration` callback (injected into `RestoreOptions`) sets `questionTcs.SetResult(costEstimate)` then awaits `answerTcs.Task` to get the user's choice.
3. The CLI event loop awaits `Task.WhenAny(pipelineTask, questionTcs.Task)`. When `questionTcs` completes, the CLI knows a question is pending. It renders cost tables and a `SelectionPrompt` on a clean console, then sets `answerTcs.SetResult(priority)`.
4. Same pattern for `ConfirmCleanup`.
5. No `AnsiConsole.Progress()` is active during the plan phase (steps 1-6). Progress display starts only for the download phase (after `ConfirmRehydration` returns).

**Rationale**: Spectre Console live components (Progress, Status, LiveDisplay) are explicitly not thread-safe. The previous approach had the pipeline thread rendering prompts while the CLI thread ran a live progress display — causing garbled output. The TCS approach ensures only one thread touches the console at a time, with clean phase boundaries. No changes to Core's pipeline or callback contracts are needed.

**Alternative considered**: Stopping the progress display before rendering prompts. Rejected because `AnsiConsole.Progress()` doesn't support pause/resume, and stopping it loses the rendering context. The TCS approach avoids starting progress in the first place during phases where prompts may appear.

### 7. AutoRefresh(true) for Spectre.Console Progress

**Decision**: Use `AnsiConsole.Progress().AutoRefresh(true)` and let Spectre handle its own refresh cycle. The display loop adds/removes/updates `ProgressTask` objects; Spectre renders them.

**Rationale**: Manual refresh adds complexity without benefit. `AutoRefresh(true)` is the documented default and works correctly. The display loop already runs on a 100ms tick to manage dynamic sub-lines — Spectre's internal refresh (default 100ms) handles the actual rendering.

### 8. Responsive poll loop with Task.WhenAny

**Decision**: Replace `await Task.Delay(100)` with `await Task.WhenAny(pipelineTask, Task.Delay(100))` in both archive and restore display loops. When the pipeline completes, the loop detects it immediately and exits.

**Rationale**: The unconditional delay means the display lags up to 100ms after pipeline completion before the loop notices. `Task.WhenAny` is the standard pattern for "wait for either completion or timeout". Zero downside, removes a perceptible delay.

### 9. Upload indeterminate-to-determinate transition

**Decision**: The Uploading `ProgressTask` starts as `IsIndeterminate = true`. When `ProgressState.TotalChunks` becomes known (set by a handler for a new event or derived when dedup completes), the display loop sets `MaxValue = TotalChunks` and `IsIndeterminate = false`. `ChunksUploaded` drives the progress value.

**Rationale**: Total chunk count is not known until the hash/dedup stage completes (chunks are created during hashing, and dedup determines which are new). A meaningful denominator cannot be displayed earlier. The indeterminate-to-determinate transition is a natural UX pattern and is natively supported by Spectre's `ProgressTask`.

## Risks / Trade-offs

- **Dual Mediator source generator references** → Both `Arius.Core.csproj` and `Arius.Cli.csproj` reference `Mediator.SourceGenerator`. This means each assembly gets its own generated registrations. The DI container must merge both. → Mitigation: Verify that `AddMediator()` discovers handlers from all assemblies in the service provider. May need to call it from CLI startup rather than Core's `AddArius`.

- **ConcurrentDictionary memory for very large archives** → With 4 hash workers and 4 upload workers, at most 8 entries exist at any time. Memory is negligible. → No mitigation needed.

- **IProgress<long> callback frequency** → `ProgressStream` reports on every `Read` call (buffer size, typically 81920 bytes). For a 5 GB file, that's ~64K callbacks. Each callback does an `Interlocked.Exchange` (~1 ns). → Negligible overhead. No throttling needed.

- **TCS complexity for restore** → The TCS approach adds coordination logic that didn't exist before. → Mitigation: The pattern is well-established in async C#. Each TCS pair is created, used once, and discarded. Unit tests will verify the phase transitions.

- **Dynamic ProgressTask lifecycle** → Adding/removing Spectre `ProgressTask` objects from within the progress context during rendering. Spectre supports this but it's less commonly used. → Mitigation: Test with concurrent operations to verify no rendering glitches.
