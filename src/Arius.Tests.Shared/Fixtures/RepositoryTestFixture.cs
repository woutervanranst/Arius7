using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Paths;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Fakes;
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
    private const    string                            TempRootFolderName = "arius";
    private readonly LocalRootPath                     _tempRoot;
    private readonly string                            _account;
    private readonly string                            _container;
    private readonly LocalRootPath                     _localRoot;
    private readonly LocalRootPath                     _restoreRoot;
    private readonly IMediator                         _mediator;
    private readonly Action<LocalRootPath>             _deleteTempRoot;
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
        LocalRootPath tempRoot,
        LocalRootPath localRoot,
        LocalRootPath restoreRoot,
        string account,
        string containerName,
        Action<LocalRootPath>? deleteTempRoot = null)
    {
        BlobContainer   = blobContainer;
        Encryption      = encryption;
        Index           = index;
        ChunkStorage    = chunkStorage;
        FileTreeService = fileTreeService;
        Snapshot        = snapshot;
        _tempRoot       = tempRoot;
        _localRoot      = localRoot;
        _restoreRoot    = restoreRoot;
        _account        = account;
        _container      = containerName;
        _deleteTempRoot = deleteTempRoot ?? (static path => path.DeleteDirectory(recursive: true));
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
    public LocalRootPath LocalRoot => _localRoot;

    /// <summary>Restore destination directory used by restore-oriented tests.</summary>
    public LocalRootPath RestoreRoot => _restoreRoot;

    /// <summary>Parent temporary directory that contains <see cref="LocalRoot"/> and <see cref="RestoreRoot"/>.</summary>
    public LocalRootPath TempRoot => _tempRoot;

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
        LocalRootPath? tempRoot = null,
        Action<LocalRootPath>? deleteTempRoot = null,
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
        LocalRootPath? tempRoot = null,
        Action<LocalRootPath>? deleteTempRoot = null,
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
    /// Writes a binary file under <see cref="LocalRoot"/> and returns the full path.
    /// The relative path is validated to stay inside the fixture source directory.
    /// </summary>
    public string WriteFile(RelativePath relativePath, byte[] content)
    {
        var full = relativePath.RootedAt(_localRoot);
        if (relativePath.Parent is { } parent)
            (_localRoot / parent).CreateDirectory();

        full.WriteAllBytes(content);
        return full.FullPath;
    }

    /// <summary>
    /// Writes a binary file under <see cref="LocalRoot"/> with explicit UTC creation and modification timestamps.
    /// Use this for archive/restore tests that assert per-path metadata preservation.
    /// </summary>
    public string WriteFile(RelativePath relativePath, byte[] content, DateTime created, DateTime modified)
    {
        var full = WriteFile(relativePath, content);
        var rootedPath = _localRoot.GetRelativePath(full).RootedAt(_localRoot);
        rootedPath.CreationTimeUtc = created;
        rootedPath.LastWriteTimeUtc = modified;
        return full;
    }

    /// <summary>
    /// Reads a restored binary file from <see cref="RestoreRoot"/>.
    /// The relative path is validated to stay inside the fixture restore directory.
    /// </summary>
    public byte[] ReadRestored(RelativePath relativePath)
        => relativePath.RootedAt(_restoreRoot).ReadAllBytes();

    /// <summary>
    /// Returns whether a restored binary file exists under <see cref="RestoreRoot"/>.
    /// The relative path is validated to stay inside the fixture restore directory.
    /// </summary>
    public bool RestoredExists(RelativePath relativePath)
        => relativePath.RootedAt(_restoreRoot).ExistsFile;

    /// <summary>
    /// Deletes the local repository cache directory for the supplied account/container pair.
    /// Use this when a test creates repository services directly but still needs standard cache cleanup.
    /// </summary>
    public static Task ResetLocalCacheAsync(string accountName, string containerName)
    {
        var cacheDir = RepositoryPaths.GetRepositoryDirectory(accountName, containerName);

        try
        {
            if (cacheDir.ExistsDirectory)
                cacheDir.DeleteDirectory(recursive: true);
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
        await ResetLocalCacheAsync(_account, _container);

        if (_tempRoot.ExistsDirectory)
            _deleteTempRoot(_tempRoot);
    }

    private static (LocalRootPath TempRoot, LocalRootPath LocalRoot, LocalRootPath RestoreRoot) CreateTempRoots(LocalRootPath? tempRoot = null)
    {
        var tempRootBase = LocalRootPath.Parse(Path.Combine(Path.GetTempPath(), TempRootFolderName));
        tempRootBase.CreateDirectory();

        var resolvedTempRoot = tempRoot ?? tempRootBase.GetSubdirectoryRoot(PathSegment.Parse($"arius-test-{Guid.NewGuid():N}"));
        var localRoot = resolvedTempRoot.GetSubdirectoryRoot(PathSegment.Parse("source"));
        var restoreRoot = resolvedTempRoot.GetSubdirectoryRoot(PathSegment.Parse("restore"));

        if (resolvedTempRoot.ExistsDirectory)
            resolvedTempRoot.DeleteDirectory(recursive: true);

        resolvedTempRoot.CreateDirectory();
        localRoot.CreateDirectory();
        restoreRoot.CreateDirectory();
        return (resolvedTempRoot, localRoot, restoreRoot);
    }
}
