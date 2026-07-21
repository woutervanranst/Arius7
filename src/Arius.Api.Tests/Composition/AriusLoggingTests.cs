using Arius.Api.Composition;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace Arius.Api.Tests.Composition;

/// <summary>
/// Drives the real Serilog pipeline built by <see cref="AriusLogging"/> to lock in the routing contract:
/// one logger, per-repository files via WriteTo.Map, host/startup events to the app-wide fallback file.
/// </summary>
public class AriusLoggingTests
{
    private static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), "arius-logtest-" + Guid.NewGuid().ToString("N"));

    private static string ReadLogFile(string dir) =>
        Directory.EnumerateFiles(dir, "arius-*.txt").Select(File.ReadAllText).FirstOrDefault() ?? "";

    [Test]
    public async Task Repo_events_route_to_the_repo_file_and_host_events_to_the_app_wide_file()
    {
        var appWideDir = NewTempDir();
        var repoDir    = NewTempDir();
        Directory.CreateDirectory(repoDir);

        try
        {
            var root = AriusLogging.BuildRootLogger(appWideDir, LogEventLevel.Information);
            // Repo-scoped logging goes through the same factory the per-job providers use.
            using (var repoFactory = AriusLogging.CreateRepositoryLoggerFactory(root, repoDir))
            {
                repoFactory.CreateLogger("RepoScoped").LogInformation("repo-line-{Marker}", "ALPHA");
                // Host/startup logging has no repo context → app-wide fallback file.
                root.Information("host-line-{Marker}", "BETA");
            }
            root.Dispose();   // flush + close all Map file sinks

            var repoLog    = ReadLogFile(repoDir);
            var appWideLog = ReadLogFile(appWideDir);

            await Assert.That(repoLog).Contains("repo-line-ALPHA");
            await Assert.That(repoLog).DoesNotContain("host-line-BETA");

            await Assert.That(appWideLog).Contains("host-line-BETA");
            await Assert.That(appWideLog).DoesNotContain("repo-line-ALPHA");

            // Line format carries the [SourceContext] class name (CLI parity).
            await Assert.That(repoLog).Contains("[RepoScoped]");
        }
        finally
        {
            Directory.Delete(appWideDir, recursive: true);
            Directory.Delete(repoDir, recursive: true);
        }
    }

    [Test]
    public async Task Root_logger_gates_on_the_configured_minimum_level()
    {
        var appWideDir = NewTempDir();
        try
        {
            var root = AriusLogging.BuildRootLogger(appWideDir, LogEventLevel.Information);
            root.Debug("debug-should-be-dropped-{M}", "X");
            root.Information("info-should-appear-{M}", "Y");
            root.Dispose();

            var log = ReadLogFile(appWideDir);
            await Assert.That(log).Contains("info-should-appear-Y");
            await Assert.That(log).DoesNotContain("debug-should-be-dropped-X");
        }
        finally
        {
            Directory.Delete(appWideDir, recursive: true);
        }
    }
}
