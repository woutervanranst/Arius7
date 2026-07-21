using Arius.Api.Composition;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace Arius.Api.Tests.Composition;

/// <summary>
/// Drives the real Serilog pipeline built by <see cref="AriusLogging"/> to lock in the ownership contract:
/// the root logger writes host/startup events to the app-wide file, each repository owns its own logger/file,
/// and disposing a repository's factory releases its file handle (so a deleted repo's logs can be removed).
/// </summary>
public class AriusLoggingTests
{
    private static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), "arius-logtest-" + Guid.NewGuid().ToString("N"));

    private static string ReadLogFile(string dir) =>
        Directory.EnumerateFiles(dir, "arius-*.txt").Select(File.ReadAllText).FirstOrDefault() ?? "";

    [Test]
    public async Task Repo_events_go_to_the_repo_file_and_host_events_to_the_app_wide_file()
    {
        var appWideDir = NewTempDir();
        var repoDir    = NewTempDir();

        try
        {
            using var root = AriusLogging.BuildRootLogger(appWideDir, LogEventLevel.Information);
            // Each repository owns its own logger/file (the per-job providers use exactly this factory).
            using (var repoFactory = AriusLogging.CreateRepositoryLoggerFactory(repoDir, LogEventLevel.Information))
            {
                repoFactory.CreateLogger("RepoScoped").LogInformation("repo-line-{Marker}", "ALPHA");
                // Host/startup logging has no repository logger → app-wide fallback file.
                root.Information("host-line-{Marker}", "BETA");
            }   // repo factory disposed → its rolling file flushed & closed

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
    public async Task Disposing_a_repo_factory_releases_its_log_file_so_the_folder_can_be_deleted()
    {
        // Regression guard for the repo-delete handle leak: the per-repo factory OWNS the rolling file, so
        // disposing it must close the handle. (On Windows an open handle would block the delete below; on
        // POSIX the delete would succeed regardless — the assertion documents the intended contract and
        // fails on Windows CI if the file is ever left open again.)
        var repoDir = NewTempDir();

        var factory = AriusLogging.CreateRepositoryLoggerFactory(repoDir, LogEventLevel.Information);
        factory.CreateLogger("RepoScoped").LogInformation("open-the-file");
        await Assert.That(Directory.EnumerateFiles(repoDir, "arius-*.txt").Any()).IsTrue();

        factory.Dispose();

        Directory.Delete(repoDir, recursive: true);
        await Assert.That(Directory.Exists(repoDir)).IsFalse();
    }

    [Test]
    public async Task Root_logger_gates_on_the_configured_minimum_level()
    {
        var appWideDir = NewTempDir();
        try
        {
            using (var root = AriusLogging.BuildRootLogger(appWideDir, LogEventLevel.Information))
            {
                root.Debug("debug-should-be-dropped-{M}", "X");
                root.Information("info-should-appear-{M}", "Y");
            }

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
