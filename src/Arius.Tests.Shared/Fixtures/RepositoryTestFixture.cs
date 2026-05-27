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
    private readonly LocalDirectory                    _tempDirectory;
    private readonly string                            _account;
    private readonly string                            _container;
    private readonly bool                              _resetLocalCacheOnDispose;
    private readonly bool                              _ownsTempRoot;
    private readonly IMediator                         _mediator;
    private readonly FakeLogger<ArchiveCommandHandler> _archiveLogger = new();
    private readonly FakeLogger<RestoreCommandHandler> _restoreLogger = new();
    private readonly FakeLogger<ListQueryHandler>      _listLogger    = new();

    /// <summary>
    /// Creates a fixture from already-constructed repository services.
    /// Prefer the static factory methods unless a test needs full control over the service graph.
    /// </summary>
    internal RepositoryTestFixture(
        IBlobContainerService blobContainer,
        IEncryptionService encryption,
        ChunkIndexService index,
        IChunkStorageService chunkStorage,
        FileTreeService fileTreeService,
        SnapshotService snapshot,
        LocalDirectory tempRoot,
        LocalDirectory localRoot,
        LocalDirectory restoreRoot,
        string account,
        string containerName,
        bool resetLocalCacheOnDispose = true,
        bool ownsTempRoot = true)
    {
        BlobContainer             = blobContainer;
        Encryption                = encryption;
        Index                     = index;
        ChunkStorage              = chunkStorage;
        FileTreeService           = fileTreeService;
        Snapshot                  = snapshot;
        _tempDirectory            = tempRoot;
        LocalDirectory            = localRoot;
        RestoreDirectory          = restoreRoot;
        LocalFileSystem           = new RelativeFileSystem(LocalDirectory);
        RestoreFileSystem         = new RelativeFileSystem(RestoreDirectory);
        _account                  = account;
        _container                = containerName;
        _resetLocalCacheOnDispose = resetLocalCacheOnDispose;
        _ownsTempRoot             = ownsTempRoot;
        _mediator                 = Substitute.For<IMediator>();
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

    /// <summary>Typed source directory used by archive-oriented tests.</summary>
    internal LocalDirectory LocalDirectory { get; }

    /// <summary>Typed restore destination directory used by restore-oriented tests.</summary>
    internal LocalDirectory RestoreDirectory { get; }

    /// <summary>Rooted filesystem for the source directory used by archive-oriented tests.</summary>
    internal RelativeFileSystem LocalFileSystem { get; }

    /// <summary>Rooted filesystem for the restore directory used by restore-oriented tests.</summary>
    internal RelativeFileSystem RestoreFileSystem { get; }

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
    internal static Task<RepositoryTestFixture> CreateWithPassphraseAsync(
        IBlobContainerService blobContainer,
        string accountName,
        string containerName,
        string? passphrase = null,
        LocalDirectory? tempRoot = null,
        bool resetLocalCacheOnDispose = true,
        CancellationToken cancellationToken = default)
    {
        var (resolvedTempRoot, localRoot, restoreRoot, ownsTempRoot) = CreateTempRoots(tempRoot);
        var encryption      = new PassphraseEncryptionService(passphrase ?? DefaultPassphrase);
        var index           = new ChunkIndexService(blobContainer, encryption, accountName, containerName);
        var chunkStorage    = new ChunkStorageService(blobContainer, encryption);
        var fileTreeService = new FileTreeService(blobContainer, encryption, index, accountName, containerName);
        var snapshot        = new SnapshotService(blobContainer, encryption, accountName, containerName);

        return Task.FromResult(new RepositoryTestFixture(blobContainer, encryption, index, chunkStorage, fileTreeService, snapshot, resolvedTempRoot, localRoot, restoreRoot, accountName, containerName, resetLocalCacheOnDispose, ownsTempRoot));
    }

    /// <summary>
    /// Creates a fixture around a caller-provided blob container and encryption service.
    /// Use this when a test must control encryption behavior explicitly, for example legacy-format
    /// compatibility tests or focused Core tests that seed serialized repository data directly.
    /// </summary>
    internal static Task<RepositoryTestFixture> CreateWithEncryptionAsync(
        IBlobContainerService blobContainer,
        string accountName,
        string containerName,
        IEncryptionService encryption,
        LocalDirectory? tempRoot = null,
        bool resetLocalCacheOnDispose = true,
        CancellationToken cancellationToken = default)
    {
        var (resolvedTempRoot, localRoot, restoreRoot, ownsTempRoot) = CreateTempRoots(tempRoot);

        var index           = new ChunkIndexService(blobContainer, encryption, accountName, containerName);
        var chunkStorage    = new ChunkStorageService(blobContainer, encryption);
        var fileTreeService = new FileTreeService(blobContainer, encryption, index, accountName, containerName);
        var snapshot        = new SnapshotService(blobContainer, encryption, accountName, containerName);

        return Task.FromResult(new RepositoryTestFixture(blobContainer, encryption, index, chunkStorage, fileTreeService, snapshot, resolvedTempRoot, localRoot, restoreRoot, accountName, containerName, resetLocalCacheOnDispose, ownsTempRoot));
    }

    /// <summary>
    /// Creates a fast unit-test fixture with in-memory storage and plaintext passthrough encryption.
    /// Use this for Core tests that need a complete repository service graph without Azurite, real
    /// Azure behavior, or passphrase encryption semantics.
    /// </summary>
    internal static Task<RepositoryTestFixture> CreateInMemoryAsync(
        string? accountName = null,
        string? containerName = null,
        LocalDirectory? tempRoot = null,
        CancellationToken cancellationToken = default)
    {
        var blobContainer = new FakeInMemoryBlobContainerService();
        return CreateWithEncryptionAsync(
            blobContainer,
            accountName ?? $"acct-test-{Guid.NewGuid():N}",
            containerName ?? $"ctr-test-{Guid.NewGuid():N}",
            new PlaintextPassthroughService(),
            tempRoot,
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

    internal static Task DeleteLocalCacheDirectoryAsync(LocalDirectory cacheDir)
    {
        var cacheFileSystem = new RelativeFileSystem(cacheDir);

        try
        {
            cacheFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }
        catch (DirectoryNotFoundException ex)
        {
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

        if (_ownsTempRoot)
            new RelativeFileSystem(_tempDirectory).DeleteDirectory(_tempDirectory, true);
    }

    private static (LocalDirectory TempRoot, LocalDirectory SourceRoot, LocalDirectory RestoreDestinationRoot, bool OwnsTempRoot) CreateTempRoots(LocalDirectory? tempRoot = null)
    {
        var ownsTempRoot       = tempRoot is null;
        var resolvedTempRoot   = tempRoot ?? TestTempRoots.CreateDirectory("test");
        var tempRootFileSystem = new RelativeFileSystem(resolvedTempRoot);
        var sourceDirectory    = resolvedTempRoot / RelativePath.Parse("source");
        var restoreDirectory   = resolvedTempRoot / RelativePath.Parse("restore");

        if (ownsTempRoot)
            tempRootFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        else
        {
            tempRootFileSystem.DeleteDirectory(sourceDirectory, recursive: true);
            tempRootFileSystem.DeleteDirectory(restoreDirectory, recursive: true);
        }

        tempRootFileSystem.CreateDirectory(RelativePath.Root);
        tempRootFileSystem.CreateDirectory(sourceDirectory);
        tempRootFileSystem.CreateDirectory(restoreDirectory);
        return (resolvedTempRoot, sourceDirectory, restoreDirectory, ownsTempRoot);
    }
}
