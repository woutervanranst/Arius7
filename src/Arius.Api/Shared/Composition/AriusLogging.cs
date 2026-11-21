using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Templates;
using Arius.Core.Shared;

namespace Arius.Api.Shared.Composition;

/// <summary>
/// Logging composition for the API. Both kinds of logger use the CLI line format
/// (<see cref="AriusLogConfig.LineTemplate"/>) and the single <c>ARIUS_LOG_LEVEL</c> level:
/// <list type="bullet">
///   <item><b>Root logger</b> (<see cref="BuildRootLogger(string)"/>): the console and an app-wide rolling file, for
///   host, startup and scheduler events and anything without a repository context.</item>
///   <item><b>Per-repository logger</b> (<see cref="CreateRepositoryLoggerFactory(string)"/>): the console and the
///   repository's rolling <c>arius-{date}.txt</c> under <c>~/.arius/{account}-{container}/logs/</c>, beside the
///   CLI's log. Owned by <see cref="RepositoryProviderRegistry"/>, which disposes it on repository delete to
///   release the file handle.</item>
/// </list>
/// </summary>
[SharedWithinAssembly]
internal static class AriusLogging
{
    private static readonly ExpressionTemplate LineTemplate = new(AriusLogConfig.LineTemplate);

    /// <summary>Global minimum level from <c>ARIUS_LOG_LEVEL</c>; <see cref="AriusLogConfig"/> guarantees a
    /// defined Serilog level name, so this parse always succeeds.</summary>
    internal static LogEventLevel ResolveLevel() =>
        Enum.Parse<LogEventLevel>(AriusLogConfig.ResolveLevelName(), ignoreCase: true);

    /// <summary>Builds the app-wide root logger. The caller owns it and must flush it on shutdown.</summary>
    internal static Serilog.Core.Logger BuildRootLogger(string appWideLogDir) =>
        BuildRootLogger(appWideLogDir, ResolveLevel());

    /// <summary>As <see cref="BuildRootLogger(string)"/> but with an explicit minimum level (test seam).</summary>
    internal static Serilog.Core.Logger BuildRootLogger(string appWideLogDir, LogEventLevel minimumLevel) =>
        BuildFileLogger(appWideLogDir, minimumLevel);

    /// <summary>Builds a repository's logger factory. Disposing it flushes and closes the repository's log file.
    /// The Microsoft.Extensions.Logging minimum level is <c>Trace</c>, so Serilog's level does all filtering.</summary>
    internal static ILoggerFactory CreateRepositoryLoggerFactory(string repoLogDir) =>
        CreateRepositoryLoggerFactory(repoLogDir, ResolveLevel());

    /// <summary>As <see cref="CreateRepositoryLoggerFactory(string)"/> but with an explicit minimum level (test seam).</summary>
    internal static ILoggerFactory CreateRepositoryLoggerFactory(string repoLogDir, LogEventLevel minimumLevel) =>
        LoggerFactory.Create(b => b
            .AddSerilog(BuildFileLogger(repoLogDir, minimumLevel), dispose: true)
            .SetMinimumLevel(LogLevel.Trace));

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
