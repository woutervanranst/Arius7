## Context

Arius.Core defines 12 notification event types published via `_mediator.Publish()` throughout the archive and restore pipelines. However, zero `INotificationHandler<T>` implementations exist — every publish is a no-op. The CLI (`CliBuilder.cs`) has a basic Spectre.Console `Progress()` for archive and `Status()` spinner for restore, but neither subscribes to Core events. Users get an indeterminate progress bar during archive and a spinner during restore with no visibility into what's happening.

The existing event types are:
- **Archive (8)**: `FileScannedEvent`, `FileHashingEvent`, `FileHashedEvent`, `ChunkUploadingEvent`, `ChunkUploadedEvent`, `TarBundleSealingEvent`, `TarBundleUploadedEvent`, `SnapshotCreatedEvent`
- **Restore (4)**: `RestoreStartedEvent`, `FileRestoredEvent`, `FileSkippedEvent`, `RehydrationStartedEvent`

Mediator is source-generated (`using Mediator;` from `Mediator.Abstractions` + `Mediator.SourceGenerator`). `INotificationHandler<T>` implementations are auto-discovered by the source generator, so registering handlers in the CLI assembly is sufficient — no manual DI wiring needed.

The `pipeline-streaming` change introduces `ProgressStream` which reports `IProgress<long>` on source bytes read. This enables per-file upload progress. A similar approach is needed for hash progress (wrapping the file stream during hashing).

## Goals / Non-Goals

**Goals:**
- Wire up `INotificationHandler<T>` for all 12 notification events
- Multi-stage Spectre.Console progress for archive: `[Scanning] [Hashing] [Uploading]` progress bars
- Scanning starts indeterminate, transitions to determinate when `FileScannedEvent` provides the total count
- Per-file progress for large files during hashing and uploading via `IProgress<long>`
- Restore progress: files restored, bytes downloaded, files remaining pending rehydration
- Shared progress state model between handlers (write) and display (read)

**Non-Goals:**
- Changing the notification event types themselves (they already carry sufficient data)
- Modifying Core pipeline logic (this is CLI-only, aside from enriching events if needed)
- Logging integration (progress display is separate from structured logging)
- Non-interactive mode (no progress bars in CI/piped output — Spectre.Console handles this natively)

## Decisions

### 1. Shared progress state class

**Decision**: Create a `ProgressState` class in Arius.Cli with thread-safe counters (using `Interlocked` operations). Notification handlers update this state; the Spectre.Console display reads it on a timer/refresh cycle. The state class is registered as a singleton in DI.

**Rationale**: Decouples event handling from display rendering. Handlers fire frequently (per-file) and must not block on UI updates. The display refreshes at a fixed rate (e.g., 10 Hz) reading the latest state. `Interlocked` operations are lock-free and sufficient for counter increments.

**Alternative considered**: Having handlers directly update Spectre.Console tasks. Rejected because Spectre.Console's `Progress` context is not thread-safe for concurrent updates from multiple handlers. A shared state intermediary is cleaner.

### 2. Spectre.Console `Progress` with custom column layout

**Decision**: Use Spectre.Console `AnsiConsole.Progress()` with three `ProgressTask` instances for archive: Scanning, Hashing, Uploading. Use `task.IsIndeterminate = true` initially for Scanning, then set `task.MaxValue` when `FileScannedEvent` arrives. For in-flight large file visibility, add a fourth section below the bars listing current operations.

**Rationale**: Spectre.Console's `Progress` API supports indeterminate→determinate transitions, concurrent task updates, and custom columns. It's already a project dependency. The `LiveDisplay` API is lower-level and would require manually rendering the entire layout — `Progress` handles the bar rendering and refresh loop.

**Alternative considered**: `LiveDisplay` with a custom `Table`. More flexible but significantly more code for the same visual result. `Progress` API is sufficient.

### 3. Notification handlers as thin state updaters

**Decision**: Each `INotificationHandler<T>` implementation is a one-liner that updates the corresponding counter/field on `ProgressState`. No business logic in handlers. Example: `FileHashedEvent` handler increments `ProgressState.FilesHashed` and sets `ProgressState.LastHashedFile`.

**Rationale**: Handlers must be fast (they run inline with pipeline work on the Mediator publish path). Moving anything beyond a counter increment into the handler would add latency to the pipeline.

### 4. Per-file progress via `IProgress<long>` callbacks

**Decision**: For large file hashing: wrap the source `FileStream` in a `ProgressStream` that reports to a callback updating `ProgressState.CurrentHashFile` and `ProgressState.CurrentHashBytesRead`. For large file uploading: the `ProgressStream` from `pipeline-streaming` reports to a similar callback updating `ProgressState.CurrentUploadFile` and `ProgressState.CurrentUploadBytesRead`. The display shows these as sub-lines:
```
  Hashing:   large-video.mp4   45% (2.1 GB / 4.7 GB)
  Uploading: backup-image.iso  78% (1.8 GB / 2.3 GB)
```

**Rationale**: The user's primary concern during long waits is "is it stuck?" — per-file progress with byte-level granularity answers this. `ProgressStream` (from `pipeline-streaming`) already provides the mechanism.

### 5. Restore progress display

**Decision**: Use Spectre.Console `Progress` for the restore download phase with tasks: "Restoring files" (determinate, from `RestoreStartedEvent.TotalFiles`), "Downloading" (bytes downloaded). On exit, print a summary: "N files restored, M files skipped, P files pending rehydration."

**Rationale**: Restore is simpler than archive — fewer concurrent operations. A single progress bar for file count and a byte counter suffices.

## Risks / Trade-offs

- **Handler frequency on large archives** → For 500K files, `FileHashedEvent` fires 500K times. Each is an `Interlocked.Increment` (~1 ns) — negligible. → Mitigation: benchmark confirms no measurable overhead.
- **Spectre.Console thread-safety** → `ProgressTask.Increment()` and `ProgressTask.Value` setter are documented as thread-safe in Spectre.Console 0.49+. → Mitigation: verify version in project dependencies.
- **Non-interactive terminals** → When `Console.Profile.Capabilities.Interactive` is false, Spectre.Console falls back to static output. → Mitigation: the existing `if (Interactive)` guard in `CliBuilder.cs` already handles this; maintain that pattern.
- **Dependency on `pipeline-streaming` for upload progress** → The `ProgressStream` is defined in the `pipeline-streaming` change. If that change is implemented later, upload progress is unavailable. → Mitigation: design the handler to gracefully degrade (show chunk count only, no byte progress) when `IProgress<long>` is not provided.
