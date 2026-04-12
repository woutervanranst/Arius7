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
using Azure.Storage.Blobs;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace Arius.E2E.Tests;

/// <summary>
/// Lightweight pipeline fixture for E2E tests backed by real Azure.
/// Mirrors the structure of PipelineFixture in integration tests.
/// </summary>
public sealed class E2EFixture : IAsyncDisposable
{
    private readonly string _tempRoot;
    private readonly BlobTier _defaultTier;
    private readonly string _account;
    private readonly string _container;
    private readonly IMediator _mediator;
    private readonly FakeLogger<ArchiveCommandHandler> _archiveLogger = new();
    private readonly FakeLogger<RestoreCommandHandler> _restoreLogger = new();

    private E2EFixture(
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
        BlobContainerClient container,
        AzureBlobContainerService svc,
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
        var account = container.AccountName;
        var index = new ChunkIndexService(svc, encryption, account, container.Name);
        var chunkStorage = new ChunkStorageService(svc, encryption);
        var fileTreeService = new FileTreeService(svc, encryption, index, account, container.Name);
        var snapshot = new SnapshotService(svc, encryption, account, container.Name);

        return new E2EFixture(
            svc,
            encryption,
            index,
            chunkStorage,
            fileTreeService,
            snapshot,
            tempRoot,
            localRoot,
            restoreRoot,
            account,
            container.Name,
            defaultTier);
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

    private ArchiveCommandHandler CreateArchiveHandler() =>
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

    private RestoreCommandHandler CreateRestoreHandler() =>
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

        var cacheDir = RepositoryPaths.GetRepositoryDirectory(_account, _container);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

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
}
