# Plan: Write CLI-style log files for Web→Core operations

## Context

When a command or query is launched from Arius Web into Arius Core, Core's `ILogger<T>`
output is routed through the API host's shared `ILoggerFactory`, whose Serilog logger
(`src/Arius.Api/Program.cs:11-14`) has **only a Console sink**. So Web-launched operations
log to the API process stdout and **nowhere else** — never to the per-repository log
directory that the CLI writes to.

The CLI, by contrast, writes a per-invocation Serilog **file** to
`~/.arius/{account}-{container}/logs/{timestamp}_{command}.txt`
via `CliBuilder.ConfigureAuditLogging(...)` (`src/Arius.Cli/CliBuilder.cs:152`), using the
public path helper `RepositoryLocalStatePaths.GetLogsDirectory(account, container)`
(`src/Arius.Core/Shared/RepositoryLocalStatePaths.cs:34`).

**Goal:** Web→Core operations (both long-running jobs *and* queries) should also be logged
into that same `logs/` directory the CLI uses, as a **per-repository rolling log** (daily,
with a 100 MB size cap), so a single log captures every operation for a repo without coupling
the log's lifetime to a provider's lifetime.

## Approach

Maintain **one shared Serilog logger per repository**, cached in `RepositoryProviderRegistry`,
writing a rolling file into the repo's `logs/` directory. Wire **both** read providers and job
providers to that logger. A single shared logger instance funnels all writes through one sink,
which is concurrency-safe (a per-provider file sink would not be) and decouples log lifetime
from the cached/disposed provider lifetime.

Read providers stay cached (they run a storage preflight + warm chunk-index/file-tree caches,
so rebuilding per query would be wasteful); only the logger is shared, not the provider.

### Changes

**1. `src/Arius.Api/Arius.Api.csproj`** — add the file-sink + formatter packages the CLI
already uses (Console + Extensions.Hosting are present):
- `Serilog.Sinks.File`
- `Serilog.Expressions`
- `Serilog.Enrichers.Thread`

**2. `src/Arius.Api/Composition/RepositoryProviderRegistry.cs`** — main change:
- Add a per-repo logger-factory cache, e.g.
  `Dictionary<long, ILoggerFactory> _repoLoggerFactories` (guarded by the existing `_gate`),
  or a `ConcurrentDictionary`.
- Add `GetOrCreateRepoLoggerFactory(repositoryId, accountName, containerName)` that builds once:
  ```csharp
  var logDir = RepositoryLocalStatePaths.GetLogsDirectory(accountName, containerName);
  Directory.CreateDirectory(logDir);
  var formatter = new ExpressionTemplate(
      "[{@t:HH:mm:ss.fff}] [{@l:u3}] [T:{ThreadId}] [{Coalesce(Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1), 'Arius')}] {@m}\n{@x}");
  var serilog = new LoggerConfiguration()
      .MinimumLevel.Information()
      .Enrich.WithThreadId()
      .WriteTo.Console()
      .WriteTo.File(
          formatter,
          Path.Combine(logDir, "arius-.txt"),
          rollingInterval: RollingInterval.Day,
          fileSizeLimitBytes: 100L * 1024 * 1024,
          rollOnFileSizeLimit: true,
          retainedFileCountLimit: 31,
          restrictedToMinimumLevel: LogEventLevel.Information)
      .CreateLogger();
  return LoggerFactory.Create(b => b.AddSerilog(serilog, dispose: true));
  ```
  (Reuse the exact format string from `CliBuilder.ConfigureAuditLogging` so CLI and Web logs
  read identically; add a comment cross-referencing it. Optional follow-up: extract the format
  string to a shared constant — Core must stay Serilog-free, so it can't live there.)
- In `BuildAsync` (after `LoadConnection`), replace the shared-factory wiring at
  lines 94-96 with the per-repo factory:
  ```csharp
  var repoLoggerFactory = GetOrCreateRepoLoggerFactory(repositoryId, connection.AccountName, connection.Container);
  services.AddSingleton(repoLoggerFactory);
  services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
  ```
  Registering an externally-created instance via `AddSingleton(instance)` means the per-job
  ServiceProvider's disposal does **not** dispose the shared logger (same pattern the code
  already relies on for `_loggerFactory`), so a finished job's `provider.DisposeAsync()` /
  `Evict(repositoryId)` leaves the rolling log open and intact.
- Dispose all cached per-repo factories in `DisposeAsync` (app shutdown) — this disposes each
  `SerilogLoggerProvider` (`dispose: true`), flushing the file. `Evict` must **not** touch
  `_repoLoggerFactories`.

The injected host `_loggerFactory` can remain for `RepositoryProviderRegistry`'s own
`_logger`; only Core's per-repo provider logging is redirected.

### Notes / non-goals
- `RepositoryLocalStatePaths.GetLogsDirectory` is already `public` — no Core change needed.
- The CLI path (`CliBuilder`) is unchanged.
- Log level stays Information+ to match the CLI.

## Verification

1. **Build:** `dotnet build src/Arius.Api/Arius.Api.csproj` (confirms the new package refs resolve).
2. **Run a query end-to-end:** start the API, open the Web app, browse a repository's snapshots
   (`/api/repos/{id}/snapshots`). Confirm a file appears at
   `~/.arius/{account}-{container}/logs/arius-{yyyyMMdd}.txt` containing Core log lines in the
   CLI format `[HH:mm:ss.fff] [INF] [T:..] [Handler] message`.
3. **Run a job end-to-end:** trigger an archive or restore from the Web UI. Confirm the same
   rolling file receives the archive/restore handler logs, and that a second job for the same
   repo appends to (does not truncate/lock) the same file.
4. **Cross-check with CLI:** run the equivalent CLI command for the same account/container and
   confirm both write into the same `logs/` directory with matching line format.
5. **Lifetime:** confirm finishing a job (which disposes the job provider and calls
   `Evict`) leaves the rolling log usable for subsequent queries; logs only flush/close fully
   on API shutdown.
