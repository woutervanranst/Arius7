using Arius.AzureBlob;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Paths;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Arius.Tests.Shared.Fixtures;
using Azure.Storage.Blobs;

namespace Arius.E2E.Tests.Fixtures;

/// <summary>
/// Lightweight pipeline fixture for E2E tests backed by real Azure.
/// Mirrors the structure of PipelineFixture in integration tests.
/// </summary>
public sealed class E2EFixture : IAsyncDisposable
{
    private static readonly Lock RepositoryCacheLeaseLock = new();
    private static readonly Dictionary<string, RepositoryCacheLease> RepositoryCacheLeases = new(StringComparer.Ordinal);
    private readonly LocalRootPath _tempRoot;
    private readonly BlobTier _defaultTier;
    private readonly string _account;
    private readonly string _container;
    private readonly RepositoryTestFixture _repository;
    private bool _disposed;

    internal E2EFixture(
        IBlobContainerService blobContainer,
        IEncryptionService encryption,
        ChunkIndexService index,
        IChunkStorageService chunkStorage,
        FileTreeService fileTreeService,
        SnapshotService snapshot,
        LocalRootPath tempRoot,
        LocalRootPath localRoot,
        LocalRootPath restoreRoot,
        string account,
        string containerName,
        BlobTier defaultTier,
        RepositoryTestFixture repository)
    {
        BlobContainer   = blobContainer;
        Encryption      = encryption;
        Index           = index;
        ChunkStorage    = chunkStorage;
        FileTreeService = fileTreeService;
        Snapshot        = snapshot;
        _tempRoot       = tempRoot;
        LocalRoot       = localRoot;
        RestoreRoot     = restoreRoot;
        _account        = account;
        _container      = containerName;
        _defaultTier    = defaultTier;
        _repository     = repository;

        lock (RepositoryCacheLeaseLock)
        {
            var cacheKey = GetRepositoryCacheKey(account, containerName);
            var lease = RepositoryCacheLeases.GetValueOrDefault(cacheKey);
            lease.LiveFixtureCount++;
            RepositoryCacheLeases[cacheKey] = lease;
        }
    }

    public IBlobContainerService BlobContainer   { get; }
    public IEncryptionService    Encryption      { get; }
    public ChunkIndexService     Index           { get; }
    public IChunkStorageService  ChunkStorage    { get; }
    public FileTreeService       FileTreeService { get; }
    public SnapshotService       Snapshot        { get; }
    public LocalRootPath         LocalRoot       { get; }
    public LocalRootPath         RestoreRoot     { get; }

    public static async Task<E2EFixture> CreateAsync(IBlobContainerService blobContainer, string accountName, string containerName, BlobTier defaultTier, string? passphrase = null, string? tempRoot = null, Action<string>? deleteTempRoot = null, CancellationToken cancellationToken = default)
    {
        var repository = await RepositoryTestFixture.CreateWithPassphraseAsync(blobContainer, accountName, containerName, passphrase, tempRoot, deleteTempRoot, cancellationToken: cancellationToken);

        return new E2EFixture(blobContainer, repository.Encryption, repository.Index, repository.ChunkStorage, repository.FileTreeService, repository.Snapshot, repository.TempRoot, repository.LocalRoot, repository.RestoreRoot, accountName, containerName, defaultTier, repository);
    }

    public static Task<E2EFixture> CreateAsync(BlobContainerClient container, AzureBlobContainerService svc, BlobTier defaultTier, string? passphrase = null, string? tempRoot = null, Action<string>? deleteTempRoot = null, CancellationToken ct = default)
    {
        return CreateAsync(svc, container.AccountName, container.Name, defaultTier, passphrase, tempRoot, deleteTempRoot, ct);
    }

    public static Task ResetLocalCacheAsync(string accountName, string containerName)
    {
        var cacheDir = RepositoryPaths.GetRepositoryDirectory(accountName, containerName);

        lock (RepositoryCacheLeaseLock)
        {
            if (HasActiveLease(accountName, containerName))
            {
                throw new InvalidOperationException(
                    $"Cannot reset local repository cache for account '{accountName}' and container '{containerName}' because an active lease exists. Dispose the active fixture before resetting the cache so workflow transitions remain explicit.");
            }

            try
            {
                if (cacheDir.ExistsDirectory)
                    cacheDir.DeleteDirectory(recursive: true);
            }
            catch (DirectoryNotFoundException ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        return Task.CompletedTask;
    }

    public Task PreserveLocalCacheAsync()
    {
        if (_disposed)
            throw new InvalidOperationException("Cannot preserve cache after fixture disposal.");

        lock (RepositoryCacheLeaseLock)
        {
            var cacheKey = GetRepositoryCacheKey(_account, _container);
            var lease = RepositoryCacheLeases.GetValueOrDefault(cacheKey);
            lease.PreserveRequested = true;
            RepositoryCacheLeases[cacheKey] = lease;
        }

        return Task.CompletedTask;
    }

    internal Task<SyntheticRepositoryState> MaterializeSourceV1Async(SyntheticRepositoryDefinition definition, int seed)
    {
        if (LocalRoot.ExistsDirectory)
            LocalRoot.DeleteDirectory(recursive: true);

        LocalRoot.CreateDirectory();

        return SyntheticRepositoryMaterializer.MaterializeV1Async(definition, seed, LocalRoot, Encryption);
    }

    public string WriteFile(RelativePath relativePath, byte[] content)
        => _repository.WriteFile(relativePath, content);

    public byte[] ReadRestored(RelativePath relativePath)
        => _repository.ReadRestored(relativePath);

    public bool RestoredExists(RelativePath relativePath)
        => _repository.RestoredExists(relativePath);

    internal ArchiveCommandHandler CreateArchiveHandler() 
        => _repository.CreateArchiveHandler();

    internal RestoreCommandHandler CreateRestoreHandler() 
        => _repository.CreateRestoreHandler();

    public Task<ArchiveResult> ArchiveAsync(CancellationToken ct = default) 
        => CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = LocalRoot,
                UploadTier    = _defaultTier,
            }),
            ct).AsTask();

    public Task<RestoreResult> RestoreAsync(CancellationToken ct = default) 
        => CreateRestoreHandler().Handle(
            new RestoreCommand(new RestoreOptions
            {
                RootDirectory = RestoreRoot,
                Overwrite     = true,
            }),
            ct).AsTask();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        Exception? tempRootDeletionException = null;
        try
        {
            await _repository.DisposeAsync();
        }
        catch (Exception ex)
        {
            tempRootDeletionException = ex;
        }

        if (ShouldResetCacheOnDispose())
            await ResetLocalCacheAsync(_account, _container);

        if (tempRootDeletionException is not null)
            throw tempRootDeletionException;

        await Task.CompletedTask;
    }

    internal static RootedPath CombineValidatedRelativePath(LocalRootPath rootPath, RelativePath relativePath)
    {
        return rootPath / relativePath;
    }

    bool ShouldResetCacheOnDispose()
    {
        lock (RepositoryCacheLeaseLock)
        {
            var cacheKey = GetRepositoryCacheKey(_account, _container);
            if (!RepositoryCacheLeases.TryGetValue(cacheKey, out var lease))
                return true;

            lease.LiveFixtureCount--;

            if (lease.LiveFixtureCount > 0)
            {
                RepositoryCacheLeases[cacheKey] = lease;
                return false;
            }

            RepositoryCacheLeases.Remove(cacheKey);
            return !lease.PreserveRequested;
        }
    }

    static bool HasActiveLease(string accountName, string containerName)
    {
        var cacheKey = GetRepositoryCacheKey(accountName, containerName);
        return RepositoryCacheLeases.TryGetValue(cacheKey, out var lease) && lease.LiveFixtureCount > 0;
    }

    static string GetRepositoryCacheKey(string accountName, string containerName) => $"{accountName}\n{containerName}";

    struct RepositoryCacheLease
    {
        public int LiveFixtureCount { get; set; }
        public bool PreserveRequested { get; set; }
    }
}
