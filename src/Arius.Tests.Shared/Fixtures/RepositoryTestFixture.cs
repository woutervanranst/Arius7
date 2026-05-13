using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Storage;
using Mediator;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using AriusLocalDirectory = Arius.Core.Shared.FileSystem.LocalDirectory;

namespace Arius.Tests.Shared.Fixtures;

/// <summary>
/// Per-test repository fixture that wires one Arius repository service graph to a caller-provided
/// storage boundary, with isolated source/restore directories and repository cache cleanup.
/// Unlike <see cref="AzuriteFixture"/>, this fixture represents one logical repository under test
/// layered on top of in-memory, Azurite, Azure, or another blob container implementation.
/// </summary>
public sealed class RepositoryTestFixture : IAsyncDisposable
{
    internal const   string                            DefaultPassphrase  = "arius-test-passphrase";
    private const    string                            TempRootFolderName = "arius";
    private readonly string                            _tempRoot;
    private readonly string                            _account;
    private readonly string                            _container;
    private readonly bool                              _resetLocalCacheOnDispose;
    private readonly bool                              _ownsTempRoot;
    private readonly IMediator                         _mediator;
    private readonly Action<string>                    _deleteTempRoot;
    private readonly FakeLogger<ArchiveCommandHandler> _archiveLogger = new();
    private readonly FakeLogger<RestoreCommandHandler> _restoreLogger = new();
    private readonly FakeLogger<ListQueryHandler>      _listLogger    = new();

    /// <summary>
    /// Creates a fixture from already-constructed repository services.
    /// Prefer the static factory methods unless a test needs full control over the service graph.
    /// </summary>
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
        bool resetLocalCacheOnDispose = true,
        bool ownsTempRoot = true,
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
        LocalDirectory   = AriusLocalDirectory.Parse(localRoot);
        RestoreDirectory = AriusLocalDirectory.Parse(restoreRoot);
        LocalFileSystem  = new RelativeFileSystem(LocalDirectory);
        RestoreFileSystem = new RelativeFileSystem(RestoreDirectory);
        _account        = account;
        _container      = containerName;
        _resetLocalCacheOnDispose = resetLocalCacheOnDispose;
        _ownsTempRoot   = ownsTempRoot;
        _deleteTempRoot = deleteTempRoot ?? (path => Directory.Delete(path, recursive: true));
        _mediator       = Substitute.For<IMediator>();
    }

    /// <summary>
    /// Storage boundary shared by the repository services in this fixture instance.
    /// This is usually fresh per test even when the underlying backend process is shared.
    /// </summary>
    public IBlobContainerService BlobContainer { get; }

    /// <summary>Encryption service used for repository serialization and chunk payloads.</summary>
    public IEncryptionService Encryption { get; }

    /// <summary>Chunk index service used for content-to-chunk lookup and mutation.</summary>
    public ChunkIndexService Index { get; }

    /// <summary>Chunk storage service used by archive and restore handlers.</summary>
    public IChunkStorageService ChunkStorage { get; }

    /// <summary>Filetree service used for reading and writing repository structure.</summary>
    public FileTreeService FileTreeService { get; }

    /// <summary>Snapshot service used for creating, listing, and resolving snapshots.</summary>
    public SnapshotService Snapshot { get; }

    /// <summary>
    /// Typed access to the fixture-owned in-memory storage fake when created with <see cref="CreateInMemoryAsync"/>.
    /// Returns <see langword="null"/> for fixtures backed by Azurite, Azure, or another custom storage fake.
    /// </summary>
    public FakeInMemoryBlobContainerService? InMemoryBlobContainer => BlobContainer as FakeInMemoryBlobContainerService;

    /// <summary>Source directory used by archive-oriented tests.</summary>
    public string LocalRoot { get; }

    /// <summary>Restore destination directory used by restore-oriented tests.</summary>
    public string RestoreRoot { get; }

    /// <summary>Typed source directory used by archive-oriented tests.</summary>
    internal AriusLocalDirectory LocalDirectory { get; }

    /// <summary>Typed restore destination directory used by restore-oriented tests.</summary>
    internal AriusLocalDirectory RestoreDirectory { get; }

    /// <summary>Rooted filesystem for the source directory used by archive-oriented tests.</summary>
    internal RelativeFileSystem LocalFileSystem { get; }

    /// <summary>Rooted filesystem for the restore directory used by restore-oriented tests.</summary>
    internal RelativeFileSystem RestoreFileSystem { get; }

    /// <summary>Parent temporary directory that contains <see cref="LocalRoot"/> and <see cref="RestoreRoot"/>.</summary>
    public string TempRoot => _tempRoot;

    /// <summary>Substitute mediator shared by handler factories so tests can inspect or ignore published events.</summary>
    public IMediator Mediator => _mediator;

    /// <summary>Repository account name used for cache paths and service wiring.</summary>
    public string AccountName => _account;

    /// <summary>Repository container name used for cache paths and service wiring.</summary>
    public string ContainerName => _container;

    public FakeLogCollector ArchiveLogs => _archiveLogger.Collector;

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
        bool resetLocalCacheOnDispose = true,
        Action<string>? deleteTempRoot = null,
        CancellationToken cancellationToken = default)
    {
        var (resolvedTempRoot, localRoot, restoreRoot, ownsTempRoot) = CreateTempRoots(tempRoot);
        var encryption      = new PassphraseEncryptionService(passphrase ?? DefaultPassphrase);
        var index           = new ChunkIndexService(blobContainer, encryption, accountName, containerName);
        var chunkStorage    = new ChunkStorageService(blobContainer, encryption);
        var fileTreeService = new FileTreeService(blobContainer, encryption, index, accountName, containerName);
        var snapshot        = new SnapshotService(blobContainer, encryption, accountName, containerName);

        return Task.FromResult(new RepositoryTestFixture(blobContainer, encryption, index, chunkStorage, fileTreeService, snapshot, resolvedTempRoot, localRoot, restoreRoot, accountName, containerName, resetLocalCacheOnDispose, ownsTempRoot, deleteTempRoot));
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
        bool resetLocalCacheOnDispose = true,
        Action<string>? deleteTempRoot = null,
        CancellationToken cancellationToken = default)
    {
        var (resolvedTempRoot, localRoot, restoreRoot, ownsTempRoot) = CreateTempRoots(tempRoot);

        var index           = new ChunkIndexService(blobContainer, encryption, accountName, containerName);
        var chunkStorage    = new ChunkStorageService(blobContainer, encryption);
        var fileTreeService = new FileTreeService(blobContainer, encryption, index, accountName, containerName);
        var snapshot        = new SnapshotService(blobContainer, encryption, accountName, containerName);

        return Task.FromResult(new RepositoryTestFixture(blobContainer, encryption, index, chunkStorage, fileTreeService, snapshot, resolvedTempRoot, localRoot, restoreRoot, accountName, containerName, resetLocalCacheOnDispose, ownsTempRoot, deleteTempRoot));
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

    /// <summary>Creates an archive handler wired to this fixture's shared repository services.</summary>
    public ArchiveCommandHandler CreateArchiveHandler() =>
        new(BlobContainer, Encryption, Index, ChunkStorage, FileTreeService, Snapshot, _mediator, _archiveLogger, _account, _container);

    /// <summary>Creates a restore handler wired to this fixture's shared repository services.</summary>
    public RestoreCommandHandler CreateRestoreHandler() =>
        new(Encryption, Index, ChunkStorage, FileTreeService, Snapshot, _mediator, _restoreLogger, _account, _container);

    /// <summary>Creates a list-query handler wired to this fixture's shared repository services.</summary>
    public ListQueryHandler CreateListQueryHandler() =>
        new(Index, FileTreeService, Snapshot, _listLogger, _account, _container);

    /// <summary>
     /// Deletes the local repository cache directory for the supplied account/container pair.
     /// Use this when a test creates repository services directly but still needs standard cache cleanup.
     /// </summary>
    public static Task ResetLocalCacheAsync(string accountName, string containerName)
        => DeleteLocalCacheDirectoryAsync(accountName, containerName);

    internal static Task DeleteLocalCacheDirectoryAsync(string accountName, string containerName)
        => DeleteLocalCacheDirectoryAsync(RepositoryPaths.GetRepositoryRoot(accountName, containerName));

    internal static Task DeleteLocalCacheDirectoryAsync(Arius.Core.Shared.FileSystem.LocalDirectory cacheDir)
    {
        try
        {
            if (Directory.Exists(cacheDir.ToString()))
                Directory.Delete(cacheDir.ToString(), recursive: true);
        }
        catch (DirectoryNotFoundException ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Releases the chunk index and removes both fixture temp directories and repository cache directories.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Index.Dispose();
        if (_resetLocalCacheOnDispose)
            await ResetLocalCacheAsync(_account, _container);

        if (_ownsTempRoot && Directory.Exists(_tempRoot))
            _deleteTempRoot(_tempRoot);
    }

    private static (string TempRoot, string LocalRoot, string RestoreRoot, bool OwnsTempRoot) CreateTempRoots(string? tempRoot = null)
    {
        var tempRootBase = Path.Combine(Path.GetTempPath(), TempRootFolderName);
        Directory.CreateDirectory(tempRootBase);

        var ownsTempRoot     = tempRoot is null;
        var resolvedTempRoot = tempRoot ?? Path.Combine(tempRootBase, $"arius-test-{Guid.NewGuid():N}");
        var localRoot        = Path.Combine(resolvedTempRoot, "source");
        var restoreRoot      = Path.Combine(resolvedTempRoot, "restore");

        if (ownsTempRoot && Directory.Exists(resolvedTempRoot))
            Directory.Delete(resolvedTempRoot, recursive: true);
        else
        {
            if (Directory.Exists(localRoot))
                Directory.Delete(localRoot, recursive: true);

            if (Directory.Exists(restoreRoot))
                Directory.Delete(restoreRoot, recursive: true);
        }

        Directory.CreateDirectory(resolvedTempRoot);
        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(restoreRoot);
        return (resolvedTempRoot, localRoot, restoreRoot, ownsTempRoot);
    }
}
