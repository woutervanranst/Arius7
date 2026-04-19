using Arius.AzureBlob;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.E2E.Tests.Datasets;
using Azure.Storage.Blobs;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace Arius.E2E.Tests.Fixtures;

/// <summary>
/// Lightweight pipeline fixture for E2E tests backed by real Azure.
/// Mirrors the structure of PipelineFixture in integration tests.
/// </summary>
public sealed class E2EFixture : IAsyncDisposable
{
    private static readonly Lock RepositoryCacheLeaseLock = new();
    private static readonly Dictionary<string, int> RepositoryCacheLiveFixtureCounts = new(StringComparer.Ordinal);
    private readonly string _tempRoot;
    private readonly BlobTier _defaultTier;
    private readonly string _account;
    private readonly string _container;
    private readonly IMediator _mediator;
    private bool _preserveLocalCache;
    private readonly FakeLogger<ArchiveCommandHandler> _archiveLogger = new();
    private readonly FakeLogger<RestoreCommandHandler> _restoreLogger = new();

    internal E2EFixture(
        IBlobContainerService blobContainer,
        IEncryptionService encryption,
        ChunkIndexService index,
        IChunkStorageService chunkStorage,
        FileTreeService fileTreeService,
        SnapshotService snapshot,
        string tempRoot,
        string localRoot,
        string restoreRoot,
        string account,
        string containerName,
        BlobTier defaultTier)
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
        _mediator = Substitute.For<IMediator>();

        lock (RepositoryCacheLeaseLock)
        {
            var cacheKey = GetRepositoryCacheKey(account, containerName);
            RepositoryCacheLiveFixtureCounts[cacheKey] =
                RepositoryCacheLiveFixtureCounts.GetValueOrDefault(cacheKey) + 1;
        }
    }

    public IBlobContainerService BlobContainer { get; }
    public IEncryptionService Encryption { get; }
    public ChunkIndexService Index { get; }
    public IChunkStorageService ChunkStorage { get; }
    public FileTreeService FileTreeService { get; }
    public SnapshotService Snapshot { get; }
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
        var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-e2e-{Guid.NewGuid():N}");
        var localRoot = Path.Combine(tempRoot, "source");
        var restoreRoot = Path.Combine(tempRoot, "restore");
        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(restoreRoot);

        var encryption = passphrase is not null
            ? (IEncryptionService)new PassphraseEncryptionService(passphrase)
            : new PlaintextPassthroughService();
        var index = new ChunkIndexService(blobContainer, encryption, accountName, containerName);
        var chunkStorage = new ChunkStorageService(blobContainer, encryption);
        var fileTreeService = new FileTreeService(blobContainer, encryption, index, accountName, containerName);
        var snapshot = new SnapshotService(blobContainer, encryption, accountName, containerName);

        return new E2EFixture(
            blobContainer,
            encryption,
            index,
            chunkStorage,
            fileTreeService,
            snapshot,
            tempRoot,
            localRoot,
            restoreRoot,
            accountName,
            containerName,
            defaultTier);
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
        lock (RepositoryCacheLeaseLock)
        {
            RepositoryCacheLiveFixtureCounts.Remove(GetRepositoryCacheKey(accountName, containerName));
        }

        var cacheDir = RepositoryPaths.GetRepositoryDirectory(accountName, containerName);

        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        return Task.CompletedTask;
    }

    public Task PreserveLocalCacheAsync()
    {
        lock (RepositoryCacheLeaseLock)
        {
            _preserveLocalCache = true;
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
    {
        var full = CombineValidatedRelativePath(LocalRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public byte[] ReadRestored(string relativePath)
        => File.ReadAllBytes(CombineValidatedRelativePath(RestoreRoot, relativePath));

    public bool RestoredExists(string relativePath)
        => File.Exists(CombineValidatedRelativePath(RestoreRoot, relativePath));

    internal ArchiveCommandHandler CreateArchiveHandler() =>
        new(
            BlobContainer,
            Encryption,
            Index,
            ChunkStorage,
            FileTreeService,
            Snapshot,
            _mediator,
            _archiveLogger,
            _account,
            _container);

    internal RestoreCommandHandler CreateRestoreHandler() =>
        new(
            Encryption,
            Index,
            ChunkStorage,
            FileTreeService,
            Snapshot,
            _mediator,
            _restoreLogger,
            _account,
            _container);

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
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);

        if (ShouldResetCacheOnDispose())
            await ResetLocalCacheAsync(_account, _container);

        await Task.CompletedTask;
    }

    internal static string CombineValidatedRelativePath(string rootPath, string relativePath)
    {
        // These helpers should only touch files under the fixture roots; rejecting rooted
        // and parent-traversal inputs keeps accidental path escapes out of test code.
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
            if (!RepositoryCacheLiveFixtureCounts.TryGetValue(cacheKey, out var liveFixtureCount))
                return true;

            liveFixtureCount--;
            if (liveFixtureCount > 0)
            {
                RepositoryCacheLiveFixtureCounts[cacheKey] = liveFixtureCount;
                return false;
            }

            RepositoryCacheLiveFixtureCounts.Remove(cacheKey);
            return !_preserveLocalCache;
        }
    }

    static string GetRepositoryCacheKey(string accountName, string containerName) =>
        $"{accountName}\n{containerName}";
}
