using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Templates;
using Arius.Core.Shared;

namespace Arius.Api.Composition;

/// <summary>
/// Logging composition for the API. Two kinds of logger, both in the shared CLI line format
/// (<see cref="AriusLogConfig.LineTemplate"/>) and both gated at the single <c>ARIUS_LOG_LEVEL</c> level:
/// <list type="bullet">
///   <item><b>Root logger</b> (<see cref="BuildRootLogger(string)"/>) — console + an app-wide rolling file for
///   host/startup/scheduler events and anything without a repository context.</item>
///   <item><b>Per-repository logger</b> (<see cref="CreateRepositoryLoggerFactory(string)"/>) — console + that
///   repository's rolling <c>arius-{date}.txt</c> under <c>~/.arius/{account}-{container}/logs/</c>, beside the
///   file the CLI writes. Owned by <see cref="RepositoryProviderRegistry"/>, which disposes it on repository
///   delete to release the file handle.</item>
/// </list>
/// </summary>
internal static class AriusLogging
{
    private static readonly ExpressionTemplate LineTemplate = new(AriusLogConfig.LineTemplate);

    /// <summary>Global minimum level from <c>ARIUS_LOG_LEVEL</c>; <see cref="AriusLogConfig"/> guarantees a
    /// defined Serilog level name, so this parse always succeeds.</summary>
    internal static LogEventLevel ResolveLevel() =>
        Enum.Parse<LogEventLevel>(AriusLogConfig.ResolveLevelName(), ignoreCase: true);

    /// <summary>Builds the app-wide root logger (console + a rolling file in <paramref name="appWideLogDir"/>) for
    /// host/startup events. The caller owns its lifetime (flush on shutdown).</summary>
    internal static Serilog.Core.Logger BuildRootLogger(string appWideLogDir) =>
        BuildRootLogger(appWideLogDir, ResolveLevel());

    /// <summary>As <see cref="BuildRootLogger(string)"/> but with an explicit minimum level (test seam).</summary>
    internal static Serilog.Core.Logger BuildRootLogger(string appWideLogDir, LogEventLevel minimumLevel) =>
        BuildFileLogger(appWideLogDir, minimumLevel);

    /// <summary>Builds a repository's own logger factory: console + a rolling file in <paramref name="repoLogDir"/>.
    /// The factory owns the Serilog logger, so disposing it flushes and closes the repository's file.
    /// <c>SetMinimumLevel(Trace)</c> defers all filtering to Serilog's global level rather than MEL's default.</summary>
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
