using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Fixtures;

namespace Arius.E2E.Tests.Fixtures;

/// <summary>
/// Lightweight pipeline fixture for E2E tests backed by real Azure.
/// Mirrors the structure of PipelineFixture in integration tests.
/// </summary>
internal sealed class E2EFixture : IAsyncDisposable
{
    private static readonly Lock RepositoryCacheLeaseLock = new();
    private static readonly Dictionary<string, RepositoryCacheLease> RepositoryCacheLeases = new(StringComparer.Ordinal);
    private readonly BlobTier _defaultTier;
    private readonly string _account;
    private readonly string _container;
    private bool _disposed;

    internal E2EFixture(
        string account,
        string containerName,
        BlobTier defaultTier,
        RepositoryTestFixture repository)
    {
        _account        = account;
        _container      = containerName;
        _defaultTier    = defaultTier;
        Repository      = repository;

        lock (RepositoryCacheLeaseLock)
        {
            var cacheKey = GetRepositoryCacheKey(account, containerName);
            var lease = RepositoryCacheLeases.GetValueOrDefault(cacheKey);
            lease.LiveFixtureCount++;
            RepositoryCacheLeases[cacheKey] = lease;
        }
    }

    public   RepositoryTestFixture Repository        { get; }
    public   IBlobContainerService BlobContainer     => Repository.BlobContainer;
    public   IEncryptionService    Encryption        => Repository.Encryption;
    internal LocalDirectory        LocalDirectory    => Repository.LocalDirectory;
    internal LocalDirectory        RestoreDirectory  => Repository.RestoreDirectory;
    internal RelativeFileSystem    LocalFileSystem   => Repository.LocalFileSystem;
    internal RelativeFileSystem    RestoreFileSystem => Repository.RestoreFileSystem;

    public static async Task<E2EFixture> CreateAsync(IBlobContainerService blobContainer, string accountName, string containerName, BlobTier defaultTier, string? passphrase = null, LocalDirectory? tempRoot = null, CancellationToken cancellationToken = default)
    {
        var repository = await RepositoryTestFixture.CreateWithPassphraseAsync(blobContainer, accountName, containerName, passphrase, tempRoot);

        return new E2EFixture(accountName, containerName, defaultTier, repository);
    }

    public static void ResetLocalCache(string accountName, string containerName)
    {
        lock (RepositoryCacheLeaseLock)
        {
            if (HasActiveLease(accountName, containerName))
            {
                throw new InvalidOperationException(
                    $"Cannot reset local repository cache for account '{accountName}' and container '{containerName}' because an active lease exists. Dispose the active fixture before resetting the cache so workflow transitions remain explicit.");
            }

            RepositoryTestFixture.DeleteLocalCacheDirectory(accountName, containerName);
        }
    }

    internal ArchiveCommandHandler CreateArchiveHandler() 
        => Repository.CreateArchiveHandler();

    internal RestoreCommandHandler CreateRestoreHandler() 
        => Repository.CreateRestoreHandler();

    public Task<ArchiveResult> ArchiveAsync(CancellationToken ct = default) 
        => CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = LocalDirectory.ToString(),
                UploadTier = _defaultTier,
            }),
            ct).AsTask();

    public Task<RestoreResult> RestoreAsync(CancellationToken ct = default) 
        => CreateRestoreHandler().Handle(
            new RestoreCommand(new RestoreOptions
            {
                RootDirectory = RestoreDirectory.ToString(),
                Overwrite = true,
            }),
            ct).AsTask();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (RepositoryCacheLeaseLock)
        {
            var cacheKey = GetRepositoryCacheKey(_account, _container);
            if (RepositoryCacheLeases.TryGetValue(cacheKey, out var lease))
            {
                lease.LiveFixtureCount--;

                if (lease.LiveFixtureCount <= 0)
                    RepositoryCacheLeases.Remove(cacheKey);
                else
                    RepositoryCacheLeases[cacheKey] = lease;
            }
        }

        await Repository.DisposeAsync();
    }

    private static bool HasActiveLease(string accountName, string containerName)
    {
        var cacheKey = GetRepositoryCacheKey(accountName, containerName);
        return RepositoryCacheLeases.TryGetValue(cacheKey, out var lease) && lease.LiveFixtureCount > 0;
    }

    private static string GetRepositoryCacheKey(string accountName, string containerName) => $"{accountName}\n{containerName}";

    private struct RepositoryCacheLease
    {
        public int LiveFixtureCount { get; set; }
    }
}
