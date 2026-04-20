using Arius.AzureBlob;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared;
using Arius.Core.Shared.Encryption;
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
    private readonly string _tempRoot;
    private readonly BlobTier _defaultTier;
    private readonly string _account;
    private readonly string _container;
    private readonly RepositoryTestFixture _repository;
    private bool _disposed;

    internal E2EFixture(
        IBlobContainerService blobContainer,
        IEncryptionService encryption,
        Arius.Core.Shared.ChunkIndex.ChunkIndexService index,
        Arius.Core.Shared.ChunkStorage.IChunkStorageService chunkStorage,
        Arius.Core.Shared.FileTree.FileTreeService fileTreeService,
        Arius.Core.Shared.Snapshot.SnapshotService snapshot,
        string tempRoot,
        string localRoot,
        string restoreRoot,
        string account,
        string containerName,
        BlobTier defaultTier,
        RepositoryTestFixture repository)
    {
        BlobContainer = blobContainer;
        Encryption = encryption;
        Index = index;
        ChunkStorage = chunkStorage;
        FileTreeService = fileTreeService;
        Snapshot = snapshot;
        _tempRoot = tempRoot;
        LocalRoot = localRoot;
        RestoreRoot = restoreRoot;
        _account = account;
        _container = containerName;
        _defaultTier = defaultTier;
        _repository = repository;

        lock (RepositoryCacheLeaseLock)
        {
            var cacheKey = GetRepositoryCacheKey(account, containerName);
            var lease = RepositoryCacheLeases.GetValueOrDefault(cacheKey);
            lease.LiveFixtureCount++;
            RepositoryCacheLeases[cacheKey] = lease;
        }
    }

    public IBlobContainerService BlobContainer { get; }
    public IEncryptionService Encryption { get; }
    public Arius.Core.Shared.ChunkIndex.ChunkIndexService Index { get; }
    public Arius.Core.Shared.ChunkStorage.IChunkStorageService ChunkStorage { get; }
    public Arius.Core.Shared.FileTree.FileTreeService FileTreeService { get; }
    public Arius.Core.Shared.Snapshot.SnapshotService Snapshot { get; }
    public string LocalRoot { get; }
    public string RestoreRoot { get; }

    public static async Task<E2EFixture> CreateAsync(
        IBlobContainerService blobContainer,
        string accountName,
        string containerName,
        BlobTier defaultTier,
        string? passphrase = null,
        CancellationToken ct = default)
    {
        var repository = await RepositoryTestFixture.CreateAsync(blobContainer, accountName, containerName, passphrase, ct: ct);

        return new E2EFixture(
            blobContainer,
            repository.Encryption,
            repository.Index,
            repository.ChunkStorage,
            repository.FileTreeService,
            repository.Snapshot,
            repository.TempRoot,
            repository.LocalRoot,
            repository.RestoreRoot,
            accountName,
            containerName,
            defaultTier,
            repository);
    }

    public static Task<E2EFixture> CreateAsync(
        BlobContainerClient container,
        AzureBlobContainerService svc,
        BlobTier defaultTier,
        string? passphrase = null,
        CancellationToken ct = default)
    {
        return CreateAsync(svc, container.AccountName, container.Name, defaultTier, passphrase, ct);
    }

    public static Task ResetLocalCacheAsync(string accountName, string containerName)
    {
        var cacheDir = RepositoryPaths.GetRepositoryDirectory(accountName, containerName);

        try
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
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

    internal Task<RepositoryTreeSnapshot> MaterializeSourceAsync(
        SyntheticRepositoryDefinition definition,
        SyntheticRepositoryVersion version,
        int seed)
    {
        if (Directory.Exists(LocalRoot))
            Directory.Delete(LocalRoot, recursive: true);

        Directory.CreateDirectory(LocalRoot);

        return SyntheticRepositoryMaterializer.MaterializeAsync(definition, version, seed, LocalRoot);
    }

    public string WriteFile(string relativePath, byte[] content)
        => _repository.WriteFile(relativePath, content);

    public byte[] ReadRestored(string relativePath)
        => _repository.ReadRestored(relativePath);

    public bool RestoredExists(string relativePath)
        => _repository.RestoredExists(relativePath);

    internal ArchiveCommandHandler CreateArchiveHandler() =>
        _repository.CreateArchiveHandler();

    internal RestoreCommandHandler CreateRestoreHandler() =>
        _repository.CreateRestoreHandler();

    public Task<ArchiveResult> ArchiveAsync(CancellationToken ct = default) =>
        CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = LocalRoot,
                UploadTier = _defaultTier,
            }),
            ct).AsTask();

    public Task<RestoreResult> RestoreAsync(CancellationToken ct = default) =>
        CreateRestoreHandler().Handle(
            new RestoreCommand(new RestoreOptions
            {
                RootDirectory = RestoreRoot,
                Overwrite = true,
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

    internal static string CombineValidatedRelativePath(string rootPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException($"Path '{relativePath}' must be relative.", nameof(relativePath));

        var parts = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Contains("..", StringComparer.Ordinal))
            throw new ArgumentException($"Path '{relativePath}' must not contain '..' segments.", nameof(relativePath));

        return Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
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

    static string GetRepositoryCacheKey(string accountName, string containerName) =>
        $"{accountName}\n{containerName}";

    struct RepositoryCacheLease
    {
        public int LiveFixtureCount { get; set; }
        public bool PreserveRequested { get; set; }
    }
}
