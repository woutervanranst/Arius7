# Logging

> **Code:** `src/Arius.Core/Shared/AriusLogConfig.cs` (the cross-host level + line-format contract), `src/Arius.Cli/CliBuilder.cs` (CLI audit setup), `src/Arius.Api/Composition/AriusLogging.cs` + `RepositoryProviderRegistry.cs` (web root + per-repo loggers), `src/Arius.Core/Features/*/...Handler.cs` (pipeline logs) · **Decisions:** [ADR-0007](../../decisions/adr-0007-separate-phase-and-detail-logging-in-pipeline-handlers.md) · **Terms:** [content hash](../../glossary.md#content-hash), [chunk index](../../glossary.md#chunk-index), [snapshot](../../glossary.md#snapshot)

## Purpose

Every `archive`, `restore`, `ls`, and `repair-index` invocation writes a complete, self-contained audit trail to disk: the full pipeline log plus a plain-text capture of everything the user saw on screen. The on-screen output stays terse; the file is the forensic record for debugging and benchmark timing.

## How it works

Two output channels, set up per invocation by each verb (`ArchiveVerb.Build`, `RestoreVerb.Build`, `LsVerb.Build`, `RepairVerb.Build`):

```mermaid
flowchart LR
  H["ILogger&lt;T&gt; call sites<br/>(handlers + shared services)"] -->|Information+| F["Serilog file sink<br/>logs/{ts}_{cmd}.txt"]
  S["AnsiConsole.MarkupLine /<br/>Live progress / tables"] --> R["Recorder<br/>(wraps AnsiConsole.Console)"]
  R -->|live| T["terminal (what the user sees)"]
  R -->|ExportText on flush| F
```

- **File sink (the audit log).** `CliBuilder.ConfigureAuditLogging(account, container, command)` sets `Log.Logger` to a single Serilog file sink at `Information` minimum level, writing to `~/.arius/{account}-{container}/logs/{yyyy-MM-dd_HH-mm-ss}_{command}.txt` (path from `RepositoryLocalStatePaths.GetLogsDirectory`, created if missing). The `update` verb skips this — it has no repository context. Every `ILogger<T>` call site across Core and the CLI feeds this one sink.
- **Console (what the user sees).** The CLI does **not** use a Serilog console sink. User-facing output is Spectre.Console (`AnsiConsole.MarkupLine`, the `Live` progress display, summary/cost tables). Each verb swaps `AnsiConsole.Console` for `AnsiConsole.Console.CreateRecorder()` for the duration of the run. The "console shows warnings and errors only" property is structural: only deliberate Spectre writes reach the screen, while informational pipeline detail goes solely to the file via `ILogger`.
- **Capture-on-flush.** In the verb's `finally`, `CliBuilder.FlushAuditLog(recorder)` calls `recorder.ExportText()` and appends it to the log under a `--- Console Output ---` header, then `Log.CloseAndFlush()`. So the file ends with an ASCII rendering of every table, progress summary, and error the user saw.

**Line format.** An `ExpressionTemplate` renders each file line as
`[{HH:mm:ss.fff}] [{u3}] [T:{ThreadId}] [{ShortSourceContext}] {Message}` (thread id from `.Enrich.WithThreadId()`, so concurrent hash/upload workers are distinguishable). `{ShortSourceContext}` is the class name peeled off the namespace-qualified Serilog `SourceContext` (`Coalesce(Substring(..., LastIndexOf '.'), 'Arius')`) — e.g. `ArchiveCommandHandler`, `FileTreeBuilder`, or `Arius` for context-less top-level crash logs.

**Two-level taxonomy in `{Message}` ([ADR-0007](../../decisions/adr-0007-separate-phase-and-detail-logging-in-pipeline-handlers.md)).** Pipeline *handlers* prefix messages with category tags; shared *services* log plain messages (identified by `{ShortSourceContext}` instead). The three levels, all visible in `ArchiveCommandHandler`:

| Level | Tag | Example |
|---|---|---|
| Lifecycle | `[archive]` / `[restore]` / `[repair]` | `[archive] Done: scanned={..} uploaded={..} size={..}` |
| Phase entry | `[phase] <name>` | `[phase] hash`, `[phase] tar-upload`, `[phase] snapshot` |
| Detail | `[hash]` `[dedup]` `[tar]` `[tree]` `[snapshot]` `[upload]` `[chunk-index]` | `[hash] {Path} -> a1b2c3d4 (4.2 MB)` |

`[phase]` markers are **entry-only** (no synthetic "complete"), because pipeline stages overlap — see ADR-0007. Detail logs exist only where they add payload beyond the phase marker.

**Formatting conventions in messages.** Hashes are truncated to 8 hex chars via `ContentHash.Short8` / `ChunkHash.Short8` / `FileTreeHash.Short8` (`Value[..8]`) — full hashes stay in the data structures and storage. Sizes are humanized with Humanizer's `bytes.Bytes().Humanize()` (`4.2 MB`), never raw byte counts.

## Key invariants

- **One log file per invocation; one global `Log.Logger`.** Each verb calls `ConfigureAuditLogging` before doing work and `FlushAuditLog` in `finally`, so the file is closed/flushed even on failure or crash (the top-level `catch` in `Program.cs` also calls `Log.Fatal` + `Log.CloseAndFlush`).
- **The file sink is the only Serilog sink in the CLI** — informational pipeline detail must never reach the terminal. New user-facing messages go through Spectre `AnsiConsole`, not `LogInformation`.
- **Hashes are truncated in logs, never elsewhere.** Truncation is a *formatting* concern (`.Short8`); persisted/in-memory hashes remain full-length.
- **Phase markers are entry points, not spans.** Don't add `[phase] X complete` logs or a detail log that merely restates a phase (ADR-0007). Durations are read by diffing the millisecond timestamps of successive markers.
- **Category tags belong to handlers, plain messages to shared services.** A service log line is attributed by `{ShortSourceContext}`, so don't push handler-style `[tag]` prefixes into shared services like `ChunkIndexService` or `FileTreeBuilder`.

## Why this shape

- The two-level phase/detail taxonomy and the no-end-marker rule are the subject of [ADR-0007](../../decisions/adr-0007-separate-phase-and-detail-logging-in-pipeline-handlers.md) — readable benchmark timing without pretending concurrent stages have sequential boundaries.
- Per-invocation file + Spectre capture means a single artifact reproduces both the trace and the operator's view, which is what you want when diagnosing a one-off archive/restore after the fact.

## Per-host setup

All three hosts emit `ILogger<T>` to a Serilog file, but the *unit* of a file and the surrounding mechanics differ. The shape above (line format, phase/detail taxonomy) is the CLI's; the web host logs per **repository** (a long-running server can't open a fresh file per call) plus one app-wide file, and the Explorer keeps its own scheme.

Two things are **shared** rather than reimplemented per host: `Arius.Core.Shared.AriusLogConfig` owns the `ARIUS_LOG_LEVEL` contract (`ResolveLevelName`) and the audit-log `LineTemplate`. It is deliberately Serilog-free — it exposes a validated level *name* and a plain template string, so Core keeps no Serilog dependency and each host feeds them to its own `LoggerConfiguration`.

| Aspect | CLI — `Arius.Cli` | Web — `Arius.Api` | Explorer — `Arius.Explorer` |
|---|---|---|---|
| Setup site | `CliBuilder.ConfigureAuditLogging` (per verb) | `AriusLogging.BuildRootLogger` (app-wide, from `AriusApiHost.AddAriusApi`) + `RepositoryProviderRegistry.GetOrCreateRepoLoggerFactory` (per repo) | `Program.Main` |
| Logging unit | per **invocation** (one verb run) | per **repository** (shared across all its operations), **plus** one app-wide file for events with no repository context | per **app launch** (one file per process) |
| Sinks | file only | rolling file **+** console | file only |
| File directory | `~/.arius/{account}-{container}/logs/` | **same** `~/.arius/{account}-{container}/logs/`; app-wide file beside the app DB (`{dirname(Arius:AppDbPath)}/logs/`) | `%LocalAppData%/Arius/logs/` |
| File name | `{yyyy-MM-dd_HH-mm-ss}_{command}.txt` | `arius-{yyyyMMdd}.txt` (+`_NNN` on overflow) | `arius-explorer-{yyyyMMdd_HHmmss}.log` |
| Rolling | none (new file per run) | daily + 100 MB cap (`rollOnFileSizeLimit`), keep 366 | none (new file per launch) |
| Min level | `AriusLogConfig.ResolveLevelName()` (`Information`) | same, one level for both loggers | same |
| Line format | `AriusLogConfig.LineTemplate` (`[ts] [u3] [T:id] [ShortSourceContext] {msg}`) | **the same constant** | own `outputTemplate` (full `SourceContext`, date + zone) |
| Lifetime / flush | `FlushAuditLog` + `Log.CloseAndFlush` in verb `finally` | root logger owned by the host (`UseSerilog(dispose: true)`); each repo factory disposed by `Remove` (delete) or `registry.DisposeAsync` (shutdown) | `Log.CloseAndFlush` at app exit |
| Spectre console capture | yes (`--- Console Output ---` footer) | no — progress via SignalR (see [hosts/web.md](../hosts/web.md)) | no |

### Which web file an event lands in

The web host runs **two kinds of logger**, both built by `AriusLogging.BuildFileLogger` (so both write the same rolling `arius-{date}.txt` in the same format). Routing is by *which DI container resolved the `ILogger<T>`*, not by a filter:

| Logger | Resolved from | Carries | File |
|---|---|---|---|
| **Root** | the host container — installed as `Log.Logger` and via `builder.Host.UseSerilog(rootLogger, dispose: true)` | ASP.NET Core framework events, `Program.cs` startup/fatal, `RepositoryProviderRegistry`'s own lines, the hosted services (`SchedulerService`, `RehydrationPollingService`, `StaleApprovalSweepService`), `JobRunner` | app-wide, beside the app DB |
| **Per-repository** | that repository's provider — `BuildAsync` registers the cached factory plus `ILogger<>` into the per-repo `ServiceCollection` | every Arius.Core handler and shared service for that repo, plus the `JobSink` `[ETA]` trace attached by `AttachJobDiagnostics` | `~/.arius/{account}-{container}/logs/` |

The app-wide file is the **fallback for events with no repository to attribute them to** — without it, host startup failures and scheduler activity would exist only on the container's stdout and vanish on restart.

The per-repo logger is **shared across both provider lifetimes** — cached read providers (browse/stats/search) and per-job providers (archive/restore) resolve the one factory in `BuildAsync` — so every Web-launched operation for a repo lands in the same file through one sink. Its lifetime is **decoupled from providers**: registered as an externally-owned singleton (`AddSingleton(instance)`), so neither `Evict` nor a job disposing its provider closes the log.

## Key invariants (web host)

- **One rolling logger per repository, shared across providers.** Don't build a file sink per provider — a job provider and a read provider for the same repo must write the same file through the same instance, or concurrent writes race and the file is split arbitrarily.
- **Logger lifetime ≠ provider lifetime.** `Evict` (after archive / on a properties change) and job-provider disposal must never dispose the per-repo logger — the repo lives on and its log keeps writing. Only registry shutdown (`DisposeAsync`) or a repository **delete** (`Remove`, which evicts the provider *and* disposes the logger) closes it.
- **A deleted repository's log handle is released, and stays released.** `Remove` disposes the factory so the file can be deleted with the repo. Two things protect that: `DELETE /repos/{id}` returns **409** while a job is active, and a per-repository **lifecycle generation** (bumped by `Remove`, re-checked in `GetOrCreateRepoLoggerFactory`) voids a provider build that was already in flight — otherwise it would cache a fresh factory under the removed id that nothing ever disposes.
- **An invalid `ARIUS_LOG_LEVEL` degrades to `Information`, never to silence.** `AriusLogConfig.ResolveLevelName` validates against the known Serilog level names and warns once on stderr. Passing an unparsed value straight to `MinimumLevel.Is` yields an undefined enum level that suppresses *all* output — the failure mode this exists to prevent.

## Open seams / future

- **Hosts still diverge in setup** (see the [per-host table](#per-host-setup)) — the *unit* of a file, the sinks, and the disposal points are per-host. The level and line format are no longer duplicated (`AriusLogConfig`), but the `LoggerConfiguration` assembly around them still is.
- **Phase durations are inferred, not recorded.** No machine-readable span data; tooling that wants exact phase timings must parse timestamps between `[phase]` lines.
- **`RestoreCommandHandler` / `ListQueryHandler` adoption.** ADR-0007 expects these to reuse the same taxonomy; code review is the enforcement mechanism (plus tests asserting the agreed coarse phase names) rather than a type-level contract.
