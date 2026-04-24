using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
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
    public string                LocalRoot       { get; }
    public string                RestoreRoot     { get; }
    public string                TempRoot        => _tempRoot;
    public IMediator             Mediator        => _mediator;
    public string                AccountName     => _account;
    public string                ContainerName   => _container;

    public static Task<RepositoryTestFixture> CreateAsync(
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

    public static Task<RepositoryTestFixture> CreateAsync(
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

        return Task.FromResult(new RepositoryTestFixture(blobContainer, encryption, index, chunkStorage, fileTreeService, snapshot, resolvedTempRoot, localRoot, restoreRoot, accountName, containerName, deleteTempRoot)); }

    public ArchiveCommandHandler CreateArchiveHandler() =>
        new(BlobContainer, Encryption, Index, ChunkStorage, FileTreeService, Snapshot, _mediator, _archiveLogger, _account, _container);

    public RestoreCommandHandler CreateRestoreHandler() =>
        new(Encryption, Index, ChunkStorage, FileTreeService, Snapshot, _mediator, _restoreLogger, _account, _container);

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

    public ValueTask DisposeAsync()
    {
        Index.Dispose();

        if (Directory.Exists(_tempRoot))
            _deleteTempRoot(_tempRoot);

        return ValueTask.CompletedTask;
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

    static (string TempRoot, string LocalRoot, string RestoreRoot) CreateTempRoots(string? tempRoot = null)
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
