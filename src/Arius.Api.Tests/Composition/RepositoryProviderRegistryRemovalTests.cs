using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.Jobs;
using Arius.Core;
using Arius.Core.Shared;
using Arius.Core.Shared.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arius.Api.Tests.Composition;

/// <summary>
/// A repository delete disposes that repository's logger factory to release its rolling log file. A provider
/// build that was already in flight must not re-register a fresh factory behind the delete — nothing would
/// ever dispose it, and the deleted repository's log file would stay open for the process lifetime.
/// </summary>
public class RepositoryProviderRegistryRemovalTests
{
    /// <summary>Blocks inside ComposeAsync until released, so a Remove can be interleaved deterministically.</summary>
    private sealed class GatedComposer : IRepositoryCoreComposer
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ComposeAsync(IServiceCollection services, RepositoryConnection connection, PreflightMode mode, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
        }
    }

    [Test]
    public async Task A_build_in_flight_when_the_repository_is_removed_cannot_register_a_logger_factory()
    {
        // Unique account/container: the logger factory would be created under ~/.arius/{account}-{container}/logs,
        // whose (non-)existence is the observable proof that no factory was built after the removal.
        var accountName   = $"arius-regtest-{Guid.NewGuid():N}";
        var containerName = "c";
        var logsDirectory = RepositoryLocalStatePaths.GetLogsDirectory(accountName, containerName);
        var databasePath  = Path.Combine(Path.GetTempPath(), $"arius-regtest-{Guid.NewGuid():N}", "app.sqlite");

        var secrets  = new SecretProtector(DataProtectionProvider.Create(nameof(RepositoryProviderRegistryRemovalTests)));
        var database = new AppDatabase(databasePath);
        var composer = new GatedComposer();

        await using var registry = new RepositoryProviderRegistry(database, secrets, composer, NullLoggerFactory.Instance);

        var accountId    = database.InsertAccount(accountName, secrets.Protect("key"));
        var repositoryId = database.InsertRepository(accountName, containerName, accountId, localPath: null, defaultTier: "Archive", encryptedPassphrase: secrets.Protect("pass"));

        try
        {
            var build = registry.GetReadProviderAsync(repositoryId, CancellationToken.None);
            await composer.Entered.Task;      // the build is past LoadConnection and parked inside ComposeAsync

            registry.Remove(repositoryId);    // repository deleted while that build is in flight
            composer.Release.SetResult();

            // The build is void: its repository is gone, so it must fault rather than cache a logger factory.
            await Assert.That(async () => await build).Throws<Exception>();
            await Assert.That(Directory.Exists(logsDirectory)).IsFalse();
        }
        finally
        {
            // TrimEnd first: GetDirectoryName of a path with a trailing separator returns the path itself,
            // which would leave the repository root (the .arius child directory) behind.
            if (Directory.Exists(logsDirectory))
                Directory.Delete(Path.GetDirectoryName(logsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!, recursive: true);

            // AppDatabase pools its connections; on Windows a pooled physical handle keeps app.sqlite open
            // and the delete below throws IOException("used by another process").
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
    }
}
