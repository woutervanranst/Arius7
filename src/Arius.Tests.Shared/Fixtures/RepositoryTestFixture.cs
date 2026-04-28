using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Storage;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace Arius.Tests.Shared.Fixtures;

public sealed class RepositoryTestFixture : IAsyncDisposable
{
    internal const   string                            DefaultPassphrase  = "arius-test-passphrase";
    private const    string                            TempRootFolderName = "arius";
    private readonly string                            _tempRoot;
    private readonly string                            _account;
    private readonly string                            _container;
    private readonly IMediator                         _mediator;
    private readonly Action<string>                    _deleteTempRoot;
    private readonly FakeLogger<ArchiveCommandHandler> _archiveLogger = new();
    private readonly FakeLogger<RestoreCommandHandler> _restoreLogger = new();
    private readonly FakeLogger<ListQueryHandler>      _listLogger    = new();

    public RepositoryTestFixture(
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
        Action<string>? deleteTempRoot = null)
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
        _deleteTempRoot = deleteTempRoot ?? (path => Directory.Delete(path, recursive: true));
        _mediator       = Substitute.For<IMediator>();
    }

    public IBlobContainerService BlobContainer   { get; }
    public IEncryptionService    Encryption      { get; }
    public ChunkIndexService     Index           { get; }
    public IChunkStorageService  ChunkStorage    { get; }
    public FileTreeService       FileTreeService { get; }
    public SnapshotService       Snapshot        { get; }
    public FakeInMemoryBlobContainerService? InMemoryBlobContainer => BlobContainer as FakeInMemoryBlobContainerService;
    public string                LocalRoot       { get; }
    public string                RestoreRoot     { get; }
    public string                TempRoot        => _tempRoot;
    public IMediator             Mediator        => _mediator;
    public string                AccountName     => _account;
    public string                ContainerName   => _container;

    /// <summary>
    /// Creates a fixture around a caller-provided blob container using normal passphrase encryption.
    /// Use this for pipeline-style tests that exercise the same encryption path as production while
    /// still controlling the storage boundary, such as Azurite-backed integration and E2E tests.
    /// </summary>
    public static Task<RepositoryTestFixture> CreateWithPassphraseAsync(
        IBlobContainerService blobContainer,
        string accountName,
        string containerName,
        string? passphrase = null,
        string? tempRoot = null,
        Action<string>? deleteTempRoot = null,
        CancellationToken cancellationToken = default)
    {
        var (resolvedTempRoot, localRoot, restoreRoot) = CreateTempRoots(tempRoot);
        var encryption      = new PassphraseEncryptionService(passphrase ?? DefaultPassphrase);
        var index           = new ChunkIndexService(blobContainer, encryption, accountName, containerName);
        var chunkStorage    = new ChunkStorageService(blobContainer, encryption);
        var fileTreeService = new FileTreeService(blobContainer, encryption, index, accountName, containerName);
        var snapshot        = new SnapshotService(blobContainer, encryption, accountName, containerName);

        return Task.FromResult(new RepositoryTestFixture(blobContainer, encryption, index, chunkStorage, fileTreeService, snapshot, resolvedTempRoot, localRoot, restoreRoot, accountName, containerName, deleteTempRoot));
    }

    /// <summary>
    /// Creates a fixture around a caller-provided blob container and encryption service.
    /// Use this when a test must control encryption behavior explicitly, for example legacy-format
    /// compatibility tests or focused Core tests that seed serialized repository data directly.
    /// </summary>
    public static Task<RepositoryTestFixture> CreateWithEncryptionAsync(
        IBlobContainerService blobContainer,
        string accountName,
        string containerName,
        IEncryptionService encryption,
        string? tempRoot = null,
        Action<string>? deleteTempRoot = null,
        CancellationToken cancellationToken = default)
    {
        var (resolvedTempRoot, localRoot, restoreRoot) = CreateTempRoots(tempRoot);

        var index           = new ChunkIndexService(blobContainer, encryption, accountName, containerName);
        var chunkStorage    = new ChunkStorageService(blobContainer, encryption);
        var fileTreeService = new FileTreeService(blobContainer, encryption, index, accountName, containerName);
        var snapshot        = new SnapshotService(blobContainer, encryption, accountName, containerName);

        return Task.FromResult(new RepositoryTestFixture(blobContainer, encryption, index, chunkStorage, fileTreeService, snapshot, resolvedTempRoot, localRoot, restoreRoot, accountName, containerName, deleteTempRoot));
    }

    /// <summary>
    /// Creates a fast unit-test fixture with in-memory storage and plaintext passthrough encryption.
    /// Use this for Core tests that need a complete repository service graph without Azurite, real
    /// Azure behavior, or passphrase encryption semantics.
    /// </summary>
    public static Task<RepositoryTestFixture> CreateInMemoryAsync(
        string? accountName = null,
        string? containerName = null,
        CancellationToken cancellationToken = default)
    {
        var blobContainer = new FakeInMemoryBlobContainerService();
        return CreateWithEncryptionAsync(
            blobContainer,
            accountName ?? $"acct-test-{Guid.NewGuid():N}",
            containerName ?? $"ctr-test-{Guid.NewGuid():N}",
            new PlaintextPassthroughService(),
            cancellationToken: cancellationToken);
    }

    public ArchiveCommandHandler CreateArchiveHandler() =>
        new(BlobContainer, Encryption, Index, ChunkStorage, FileTreeService, Snapshot, _mediator, _archiveLogger, _account, _container);

    public RestoreCommandHandler CreateRestoreHandler() =>
        new(Encryption, Index, ChunkStorage, FileTreeService, Snapshot, _mediator, _restoreLogger, _account, _container);

    public ListQueryHandler CreateListQueryHandler() =>
        new(Index, FileTreeService, Snapshot, _listLogger, _account, _container);

    public string WriteFile(string relativePath, byte[] content)
    {
        var full = CombineValidatedRelativePath(LocalRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string WriteFile(string relativePath, byte[] content, DateTime created, DateTime modified)
    {
        var full = WriteFile(relativePath, content);
        File.SetCreationTimeUtc(full, created);
        File.SetLastWriteTimeUtc(full, modified);
        return full;
    }

    public byte[] ReadRestored(string relativePath)
        => File.ReadAllBytes(CombineValidatedRelativePath(RestoreRoot, relativePath));

    public bool RestoredExists(string relativePath)
        => File.Exists(CombineValidatedRelativePath(RestoreRoot, relativePath));

    public static Task ResetLocalCacheAsync(string accountName, string containerName)
    {
        var cacheDir = RepositoryPaths.GetRepositoryDirectory(accountName, containerName);

        try
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, recursive: true);
        }
        catch (DirectoryNotFoundException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Index.Dispose();
        await ResetLocalCacheAsync(_account, _container);

        if (Directory.Exists(_tempRoot))
            _deleteTempRoot(_tempRoot);
    }

    private static string CombineValidatedRelativePath(string root, string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var normalizedRoot = Path.GetFullPath(root);

        if (!combined.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(combined, normalizedRoot, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(relativePath), "Path must stay within the fixture root.");
        }

        return combined;
    }

    private static (string TempRoot, string LocalRoot, string RestoreRoot) CreateTempRoots(string? tempRoot = null)
    {
        var tempRootBase = Path.Combine(Path.GetTempPath(), TempRootFolderName);
        Directory.CreateDirectory(tempRootBase);

        var resolvedTempRoot = tempRoot ?? Path.Combine(tempRootBase, $"arius-test-{Guid.NewGuid():N}");
        var localRoot        = Path.Combine(resolvedTempRoot, "source");
        var restoreRoot      = Path.Combine(resolvedTempRoot, "restore");

        if (Directory.Exists(resolvedTempRoot))
            Directory.Delete(resolvedTempRoot, recursive: true);

        Directory.CreateDirectory(resolvedTempRoot);
        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(restoreRoot);
        return (resolvedTempRoot, localRoot, restoreRoot);
    }
}
