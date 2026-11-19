using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Core.Shared.Storage;
using Arius.Core.Shared;
using Arius.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arius.Api.Tests.Shared.Composition;

/// <summary>
/// Deleting a repository disposes its logger factory to release its log file. A provider build already in flight
/// must not register a new factory after the delete: nothing would dispose it, and the log file would stay open.
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

        public void ComposeWithoutRepository(IServiceCollection services) { }
    }

    [Test]
    public async Task A_build_in_flight_when_the_repository_is_removed_cannot_register_a_logger_factory()
    {
        // A unique account name: ~/.arius/{account}-{container}/logs exists only if a logger factory was created.
        var accountName   = $"arius-regtest-{Guid.NewGuid():N}";
        var containerName = "c";
        var logsDirectory = RepositoryLocalStatePaths.GetLogsDirectory(accountName, containerName);
        var databasePath  = Path.Combine(Path.GetTempPath(), $"arius-regtest-{Guid.NewGuid():N}", "app.sqlite");

        var secrets  = new SecretProtector(DataProtectionProvider.Create(nameof(RepositoryProviderRegistryRemovalTests)));
        var database = new AppDatabase(databasePath);
        var composer = new GatedComposer();

        await using var registry = new RepositoryProviderRegistry(database, secrets, composer, NullLoggerFactory.Instance);

        var accountId    = database.InsertAccount(accountName, secrets.Protect("key"));
        var repositoryId = database.InsertRepository(accountName, containerName, accountId, localPath: null, encryptedPassphrase: secrets.Protect("pass"));

        try
        {
            var build = registry.GetReadProviderAsync(repositoryId, CancellationToken.None);
            await composer.Entered.Task;      // the build is blocked inside ComposeAsync

            registry.Remove(repositoryId);
            composer.Release.SetResult();

            // The repository is gone, so the build must fault instead of creating a logger factory.
            await Assert.That(async () => await build).Throws<Exception>();
            await Assert.That(Directory.Exists(logsDirectory)).IsFalse();
        }
        finally
        {
            // TrimEnd first: GetDirectoryName of a path with a trailing separator returns the path itself.
            if (Directory.Exists(logsDirectory))
                Directory.Delete(Path.GetDirectoryName(logsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!, recursive: true);

            // On Windows a pooled connection keeps app.sqlite open, so clear the pools before deleting.
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
    }
}
