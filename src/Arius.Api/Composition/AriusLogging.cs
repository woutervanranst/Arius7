using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Templates;

namespace Arius.Api.Composition;

/// <summary>
/// The single logging composition for the whole API. One Serilog logger is built here at the composition
/// root (<see cref="AriusApiHost.AddAriusApi"/>), wired to the host via <c>UseSerilog</c> AND handed to
/// <see cref="RepositoryProviderRegistry"/> for the per-job providers — so every log line in the process
/// (host startup, scheduler, browse queries, Core archive/restore handlers, and the job sinks' <c>[ETA]</c>
/// trace) flows through the same pipeline.
///
/// Routing is by the <see cref="RepoLogDirProperty"/> log-context property via a <c>WriteTo.Map</c> sink:
/// events tagged with a repository's logs directory land in that repo's rolling <c>arius-{date}.txt</c>
/// (the same file the CLI writes beside); everything without the property (host/startup) falls to the
/// app-wide <c>defaultKey</c> file. The console sink sees everything. One minimum level, from
/// <c>ARIUS_LOG_LEVEL</c> (default Information), gates the whole pipeline — nothing is gated on Debug.
/// </summary>
internal static class AriusLogging
{
    /// <summary>Log-context property carrying a repository's logs directory; <c>WriteTo.Map</c> routes each
    /// event to <c>{value}/arius-{date}.txt</c>. Absent on host/startup events → they hit the app-wide file.</summary>
    internal const string RepoLogDirProperty = "RepoLogDir";

    // Mirrors the CLI's audit-log line format (Arius.Cli.CliBuilder.ConfigureAuditLogging) so CLI and API
    // logs read identically; [SourceContext] is rendered as the class name (last '.'-segment).
    private static readonly ExpressionTemplate LineTemplate = new(
        "[{@t:HH:mm:ss.fff}] [{@l:u3}] [T:{ThreadId}] [{Coalesce(Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1), 'Arius')}] {@m}\n{@x}");

    /// <summary>Global minimum level from <c>ARIUS_LOG_LEVEL</c> (Verbose/Debug/Information/Warning/Error/Fatal); default Information.</summary>
    internal static LogEventLevel ResolveLevel() =>
        Enum.TryParse<LogEventLevel>(Environment.GetEnvironmentVariable("ARIUS_LOG_LEVEL")?.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : LogEventLevel.Information;

    /// <summary>Builds the one process-wide Serilog logger: console + a repo-routed rolling file (host/startup
    /// events go to <paramref name="appWideLogDir"/>). The caller owns its lifetime (flush on shutdown).
    /// Minimum level comes from <c>ARIUS_LOG_LEVEL</c> (default Information).</summary>
    internal static Serilog.Core.Logger BuildRootLogger(string appWideLogDir) =>
        BuildRootLogger(appWideLogDir, ResolveLevel());

    /// <summary>As <see cref="BuildRootLogger(string)"/> but with an explicit minimum level (test seam — the
    /// production call site reads <c>ARIUS_LOG_LEVEL</c>).</summary>
    internal static Serilog.Core.Logger BuildRootLogger(string appWideLogDir, LogEventLevel minimumLevel)
    {
        Directory.CreateDirectory(appWideLogDir);

        return new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.WithThreadId()
            .Enrich.FromLogContext()
            .WriteTo.Console(LineTemplate)
            .WriteTo.Map(
                keyPropertyName: RepoLogDirProperty,
                defaultKey:      appWideLogDir,
                configure: (logDir, wt) => wt.File(
                    LineTemplate,
                    Path.Combine(logDir, "arius-.txt"),
                    rollingInterval:        RollingInterval.Day,
                    fileSizeLimitBytes:     100L * 1024 * 1024,
                    rollOnFileSizeLimit:    true,
                    retainedFileCountLimit: 366))
            .CreateLogger();
    }

    /// <summary>Wraps the shared root logger in an MEL <see cref="ILoggerFactory"/> that tags every event with
    /// <paramref name="repoLogDir"/>, so a per-job provider's loggers route to that repository's file. Does NOT
    /// own the root logger (<c>dispose: false</c>); <c>SetMinimumLevel(Trace)</c> defers all filtering to
    /// Serilog's single global level rather than MEL's Information default.</summary>
    internal static ILoggerFactory CreateRepositoryLoggerFactory(Serilog.ILogger rootLogger, string repoLogDir) =>
        LoggerFactory.Create(b => b
            .AddSerilog(rootLogger.ForContext(RepoLogDirProperty, repoLogDir), dispose: false)
            .SetMinimumLevel(LogLevel.Trace));
}
