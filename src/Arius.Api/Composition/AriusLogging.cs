using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Templates;
using Arius.Core.Shared;

namespace Arius.Api.Composition;

/// <summary>
/// Logging composition for the whole API. Two kinds of logger, both built here and both in the shared CLI
/// line format (<see cref="AriusLogConfig.LineTemplate"/>):
/// <list type="bullet">
///   <item><b>Root logger</b> (<see cref="BuildRootLogger(string)"/>) — console + an app-wide rolling file;
///   carries host/startup/scheduler events and events without a repository context. Wired to the host via
///   <c>UseSerilog</c> and owned by the composition root.</item>
///   <item><b>Per-repository logger</b> (<see cref="CreateRepositoryLoggerFactory(string)"/>) — console + that
///   repository's rolling <c>arius-{date}.txt</c> under <c>~/.arius/{account}-{container}/logs/</c> (the same
///   file the CLI writes beside). Each repository owns its own logger, so <see cref="RepositoryProviderRegistry"/>
///   disposing it on repository <b>delete</b> flushes and CLOSES the file — releasing the handle rather than
///   holding it for the whole process.</item>
/// </list>
/// One minimum level, from <c>ARIUS_LOG_LEVEL</c> (default Information), gates every logger; nothing is gated on
/// Debug.
/// </summary>
internal static class AriusLogging
{
    // Mirrors the CLI's audit-log line format so CLI and API logs read identically; [SourceContext] is rendered
    // as the class name (last '.'-segment).
    private static readonly ExpressionTemplate LineTemplate = new(AriusLogConfig.LineTemplate);

    /// <summary>Global minimum level from <c>ARIUS_LOG_LEVEL</c> (Verbose/Debug/Information/Warning/Error/Fatal);
    /// default Information. An invalid value falls back to Information (see <see cref="AriusLogConfig"/>) — the
    /// resolved name is always a defined level, so this parse never yields an out-of-range enum.</summary>
    internal static LogEventLevel ResolveLevel() =>
        Enum.Parse<LogEventLevel>(AriusLogConfig.ResolveLevelName(), ignoreCase: true);

    /// <summary>Builds the app-wide root logger (console + a rolling file in <paramref name="appWideLogDir"/>) for
    /// host/startup events. The caller owns its lifetime (flush on shutdown). Minimum level from
    /// <c>ARIUS_LOG_LEVEL</c>.</summary>
    internal static Serilog.Core.Logger BuildRootLogger(string appWideLogDir) =>
        BuildRootLogger(appWideLogDir, ResolveLevel());

    /// <summary>As <see cref="BuildRootLogger(string)"/> but with an explicit minimum level (test seam — the
    /// production call site reads <c>ARIUS_LOG_LEVEL</c>).</summary>
    internal static Serilog.Core.Logger BuildRootLogger(string appWideLogDir, LogEventLevel minimumLevel) =>
        BuildFileLogger(appWideLogDir, minimumLevel);

    /// <summary>Builds a repository's OWN logger factory: console + a rolling file in <paramref name="repoLogDir"/>,
    /// same format and level as the root logger. The returned factory OWNS the Serilog logger (<c>dispose: true</c>)
    /// — disposing it flushes and closes the repo's rolling file, so a deleted repository releases its handle.
    /// <c>SetMinimumLevel(Trace)</c> defers all filtering to Serilog's single global level rather than MEL's
    /// Information default.</summary>
    internal static ILoggerFactory CreateRepositoryLoggerFactory(string repoLogDir) =>
        CreateRepositoryLoggerFactory(repoLogDir, ResolveLevel());

    /// <summary>As <see cref="CreateRepositoryLoggerFactory(string)"/> but with an explicit minimum level (test seam).</summary>
    internal static ILoggerFactory CreateRepositoryLoggerFactory(string repoLogDir, LogEventLevel minimumLevel) =>
        LoggerFactory.Create(b => b
            .AddSerilog(BuildFileLogger(repoLogDir, minimumLevel), dispose: true)
            .SetMinimumLevel(LogLevel.Trace));

    /// <summary>Builds one Serilog logger writing to the console plus a daily-rolling <c>arius-{date}.txt</c> in
    /// <paramref name="logDir"/>, gated at <paramref name="minimumLevel"/>. Shared by the root and per-repo loggers.</summary>
    private static Serilog.Core.Logger BuildFileLogger(string logDir, LogEventLevel minimumLevel)
    {
        Directory.CreateDirectory(logDir);

        return new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.WithThreadId()
            .Enrich.FromLogContext()
            .WriteTo.Console(LineTemplate)
            .WriteTo.File(
                LineTemplate,
                Path.Combine(logDir, "arius-.txt"),
                rollingInterval:        RollingInterval.Day,
                fileSizeLimitBytes:     100L * 1024 * 1024,
                rollOnFileSizeLimit:    true,
                retainedFileCountLimit: 366)
            .CreateLogger();
    }
}
