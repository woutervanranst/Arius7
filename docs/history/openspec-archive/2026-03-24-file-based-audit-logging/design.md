## Context

Arius currently has two output channels, both going to stdout:
1. `ILogger<T>` via `Microsoft.Extensions.Logging.Console` — 15 call sites (12 warnings, 3 errors) in Arius.Core pipeline handlers
2. `Spectre.Console.AnsiConsole` — 34 call sites in `CliBuilder.cs` for user-facing progress, results, and interactive prompts

There is no persistent record of what happened during an operation. The `cli` spec defines Mediator notification events (`FileScanned`, `FileHashed`, `ChunkUploaded`, etc.) for driving the Spectre.Console progress display — these serve a different purpose (UI) than the audit trail (diagnostics).

This change depends on `human-readable-repo-paths` for the `~/.arius/{account}-{container}/` directory structure.

## Goals / Non-Goals

**Goals:**
- Provide a greppable, per-file audit trail of every archive/restore/ls operation
- Make it easy to trace a specific file's journey through the pipeline
- Capture both ILogger diagnostic output and Spectre.Console user-facing output in a single log file per invocation
- Keep the terminal experience unchanged (quiet, Warning+ only on console)

**Non-Goals:**
- Log rotation or cleanup (future concern)
- Structured/JSON logging (plain text is fine for now)
- Replacing the Spectre.Console progress display with ILogger (they serve different purposes)
- Implementing `INotificationHandler<T>` for the Mediator events (separate concern, future work)

## Decisions

### Decision 1: Serilog with file sink

**Choice:** Replace `Microsoft.Extensions.Logging.Console` with Serilog. Configure two sinks: console (Warning+) and file (Information+).

**Alternatives considered:**
- Custom `ILoggerProvider` (~50 lines) — lightweight but no ecosystem (no rolling, no formatting options)
- `NLog` — heavier than needed, less idiomatic in modern .NET
- Keep `Microsoft.Extensions.Logging` and add a community file provider — fragmented ecosystem, Serilog is the standard

**Rationale:** Serilog is the de facto standard for .NET logging. `Serilog.Sinks.File` is battle-tested and lightweight. The `Serilog.Extensions.Logging` bridge integrates cleanly with `Microsoft.Extensions.Logging` so all existing `ILogger<T>` call sites work unchanged.

**New dependencies:** `Serilog`, `Serilog.Extensions.Logging`, `Serilog.Sinks.Console`, `Serilog.Sinks.File` in Arius.Cli. `Humanizer.Core` added to Arius.Core (already in Arius.Cli).

### Decision 2: Direct `LogInformation` calls in pipeline handlers (not notification handlers)

**Choice:** Add `_logger.LogInformation(...)` calls directly at each pipeline stage in `ArchivePipelineHandler`, `RestorePipelineHandler`, `LsHandler`, and `LocalFileEnumerator`.

**Alternatives considered:**
- Implement `INotificationHandler<T>` for each Mediator event — decoupled but events lack file-path context in upload stages, would need enrichment. Also, these handlers are intended for the progress display per the `cli` spec.

**Rationale:** The logging context (file paths, sizes, hashes) is immediately available in scope at each stage. No need to shuttle it through event types. The notification events remain reserved for their intended purpose (Spectre.Console progress display). Direct calls are simpler, more readable, and colocated with the logic.

### Decision 3: File path as trace key, stage tags, thread ID

**Choice:** Use the file's relative path as the natural trace key (no synthetic correlation ID). Tag each log line with a stage identifier (`[hash]`, `[dedup]`, `[upload]`, `[tar]`, `[index]`, `[tree]`, `[snapshot]`, `[pointer]`, `[scan]`). Include thread ID via Serilog's `{ThreadId}` enricher.

**Rationale:** One log file = one invocation, so no operation-level ID is needed. The relative path is human-readable, unique within a run, and available at every pipeline stage. Stage tags provide a second dimension for filtering. Thread ID is automatic and useful for debugging concurrency issues with the channel-based pipeline's 15 concurrent tasks.

**Log line format:**
```
[14:30:00.015] [INF] [hash] [T:12] photos/2024/sunset.jpg -> a1b2c3d4 (4.2 MB)
```

### Decision 4: Hash truncation to 8 characters

**Choice:** Display first 8 hex characters of SHA-256 hashes in log messages (e.g. `a1b2c3d4` instead of the full 64-char hash). Implemented as a helper extension method `hash.Short()` or inline `hash[..8]`.

**Rationale:** 8 hex chars = 4 billion possible values. For typical repos (thousands to tens of thousands of files), collisions are impossible in practice. Matches git's convention. Full hashes remain in the underlying data structures.

### Decision 5: Humanizer for sizes in Core

**Choice:** Add `Humanizer.Core` as a dependency to `Arius.Core` so pipeline handlers can log humanized sizes (e.g. `4.2 MB` instead of `4404019`).

**Rationale:** `Humanizer.Core` is already in the project (`Arius.Cli` and `Arius.AzureBlob`). Adding it to Core is a lightweight dependency that significantly improves log readability.

### Decision 6: Spectre.Console Recorder for output capture

**Choice:** Wrap `AnsiConsole.Console` with `new Recorder(AnsiConsole.Console)` at the start of each command handler. Use `recorder.*` instead of `AnsiConsole.*` for all output. At the end of the command, call `recorder.ExportText()` and append the plain-text output to the log file.

**Rationale:** `Recorder` is built into Spectre.Console (already at v0.54.0). It tees output — everything still goes to the terminal via the wrapped console, AND is captured for later export. `ExportText()` strips ANSI markup, producing clean plain text suitable for the log. Tables are preserved as ASCII art. This avoids duplicating every `AnsiConsole` call with an `ILogger` call inline.

**Implementation:** All 34 `AnsiConsole.*` call sites in `CliBuilder.cs` change to `recorder.*`. The recorder is created at command handler entry and the captured text is flushed to the logger at command handler exit.

### Decision 7: Log file naming and location

**Choice:** `~/.arius/{account}-{container}/logs/{yyyy-MM-dd_HH-mm-ss}_{command}.txt`

Example: `~/.arius/mystorageacct-photos/logs/2026-03-24_14-30-00_archive.txt`

**Rationale:** Per-invocation files are simple, greppable, and self-contained. The timestamp gives chronological ordering. The command name makes it scannable. The `.txt` extension is universally openable.

## Risks / Trade-offs

- **[Log file accumulation]** → Log files grow unbounded over time. Mitigation: deferred to future work. Individual files are small (text logs of short operations). Users can manually purge `logs/` directories.
- **[Performance of per-file logging]** → Logging every file at every stage adds I/O. For 100k files, that's potentially 500k+ log lines. Mitigation: Serilog's async file sink is performant. The pipeline is network-bound (Azure uploads), not disk-bound. Log writes are negligible relative to archive/restore operations.
- **[Recorder refactoring scope]** → Changing all 34 `AnsiConsole.*` call sites to `recorder.*` is mechanical but touches every command handler in `CliBuilder.cs`. Mitigation: it's a single file, the changes are uniform (search-and-replace), and the `Recorder` wraps the console transparently.
- **[Humanizer in Core]** → Adds a dependency to the core library. Mitigation: `Humanizer.Core` is a well-maintained, small package with no transitive dependencies. It's already used in two other projects in the solution.
