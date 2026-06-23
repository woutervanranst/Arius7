# Logging

> **Code:** `src/Arius.Cli/CliBuilder.cs` (CLI audit setup), `src/Arius.Api/Composition/RepositoryProviderRegistry.cs` (web per-repo rolling log), `src/Arius.Core/Features/*/...Handler.cs` (pipeline logs) · **Decisions:** [ADR-0007](../../decisions/adr-0007-separate-phase-and-detail-logging-in-pipeline-handlers.md) · **Terms:** [content hash](../../glossary.md#content-hash), [chunk index](../../glossary.md#chunk-index), [snapshot](../../glossary.md#snapshot)

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

## The web host: one rolling file per repository

`Arius.Api` reuses the same on-disk location and line format, but the unit is the **repository**, not the invocation — a long-running server can't open a fresh file per call. `RepositoryProviderRegistry.GetOrCreateRepoLoggerFactory(repositoryId, account, container)` builds **one shared Serilog logger per repository**, cached for the registry's lifetime, with two sinks: `WriteTo.Console()` plus a `WriteTo.File` **rolling** sink into the *same* `~/.arius/{account}-{container}/logs/` directory the CLI uses (`RepositoryLocalStatePaths.GetLogsDirectory`). It uses the identical `ExpressionTemplate` line format (copied from `ConfigureAuditLogging`, with a cross-reference comment), so CLI and web logs read the same.

- **Rolling, not per-invocation.** File name `arius-.txt` with `rollingInterval: Day`, a `100 MB` size cap (`rollOnFileSizeLimit`), and `retainedFileCountLimit: 366` (≈ one year of daily files) → `logs/arius-20260621.txt`, `arius-20260621_001.txt` on overflow, `arius-20260622.txt`, …
- **Shared across both provider lifetimes and across operations.** Both cached read providers (browse/stats/search queries) and per-job providers (archive/restore) resolve this one factory in `BuildAsync`, so every Web-launched operation for a repo lands in the same rolling file — not just the console. A single logger instance also funnels all writes through one sink, which is concurrency-safe across simultaneous jobs/queries on the same repo.
- **Lifetime decoupled from providers.** The factory is registered as an externally-owned instance (`AddSingleton(instance)`), so a finished job disposing its provider — or `Evict` dropping a cached read provider — does **not** close the repo's rolling log. The factories are disposed (flushing the file via `AddSerilog(serilog, dispose: true)`) only in `RepositoryProviderRegistry.DisposeAsync` at app shutdown, after the providers, so provider-disposal logging still lands.

There is **no Spectre capture / `--- Console Output ---` footer** here — that is a CLI-only mechanism; the web host surfaces operator-facing progress through SignalR (see [hosts/web.md](../hosts/web.md)), and the file is purely the `ILogger<T>` trace.

## Key invariants (web host)

- **One rolling logger per repository, shared across providers.** Don't build a file sink per provider — a job provider and a read provider for the same repo must write the same file through the same instance, or concurrent writes race and the file is split arbitrarily.
- **Logger lifetime ≠ provider lifetime.** `Evict` / job-provider disposal must never dispose the per-repo logger; only registry shutdown does.

## Open seams / future

- **Hosts still diverge in setup.** All three hosts now write the same line format to `~/.arius/.../logs/`, but `Arius.Cli` is per-invocation (+ Spectre capture), `Arius.Api` is per-repository rolling, and `Arius.Explorer` uses `WriteTo.File` at `Debug` with its own template. The format string is duplicated (CLI ↔ Api) rather than shared — Core stays Serilog-free, so a shared package would have to live outside Core. Unifying setup would remove the per-host duplication.
- **Phase durations are inferred, not recorded.** No machine-readable span data; tooling that wants exact phase timings must parse timestamps between `[phase]` lines.
- **`RestoreCommandHandler` / `ListQueryHandler` adoption.** ADR-0007 expects these to reuse the same taxonomy; code review is the enforcement mechanism (plus tests asserting the agreed coarse phase names) rather than a type-level contract.
