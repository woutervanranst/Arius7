using Arius.Api.Shared.Composition;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace Arius.Api.Tests.Shared.Composition;

/// <summary>
/// Exercises the Serilog pipeline built by <see cref="AriusLogging"/>: the root logger writes host events to the
/// app-wide file, each repository has its own logger and file, and disposing a repository's factory releases its
/// file so a deleted repository's logs can be removed.
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
            // Dispose both loggers before reading their files: a live Serilog file sink opens its file with
            // FileShare.Read, so File.ReadAllText fails on Windows.
            using (var root = AriusLogging.BuildRootLogger(appWideDir, LogEventLevel.Information))
            using (var repoFactory = AriusLogging.CreateRepositoryLoggerFactory(repoDir, LogEventLevel.Information))
            {
                repoFactory.CreateLogger("RepoScoped").LogInformation("repo-line-{Marker}", "ALPHA");
                root.Information("host-line-{Marker}", "BETA");
            }

            var repoLog    = ReadLogFile(repoDir);
            var appWideLog = ReadLogFile(appWideDir);

            await Assert.That(repoLog).Contains("repo-line-ALPHA");
            await Assert.That(repoLog).DoesNotContain("host-line-BETA");

            await Assert.That(appWideLog).Contains("host-line-BETA");
            await Assert.That(appWideLog).DoesNotContain("repo-line-ALPHA");

            // The line format includes the [SourceContext] class name, as in the CLI.
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
        // Disposing the repository factory must close its log file. Only Windows blocks deleting an open file,
        // so on other platforms this test cannot detect a leaked handle.
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
