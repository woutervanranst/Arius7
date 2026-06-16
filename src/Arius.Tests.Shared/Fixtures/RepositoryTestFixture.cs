using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Compression;
using Arius.Tests.Shared.Encryption;
using Arius.Tests.Shared.Storage;
using Mediator;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace Arius.Tests.Shared.Fixtures;

/// <summary>
/// Per-test repository fixture that wires one Arius repository service graph to a caller-provided
/// storage boundary, with isolated source/restore directories and repository cache cleanup.
/// Unlike <see cref="AzuriteFixture"/>, this fixture represents one logical repository under test
/// layered on top of in-memory, Azurite, Azure, or another blob container implementation.
/// </summary>
internal sealed class RepositoryTestFixture : IAsyncDisposable
{
    private readonly FakeLogger<ArchiveCommandHandler> _archiveLogger = new();
    private readonly FakeLogger<RestoreCommandHandler> _restoreLogger = new();
    private readonly FakeLogger<ListQueryHandler>      _listLogger    = new();
    private readonly List<ChunkIndexService>          _ownedIndexes  = [];

    /// <summary>
    /// Creates a fixture around a caller-provided blob container using normal passphrase encryption.
    /// Use this for pipeline-style tests that exercise the same encryption path as production while
    /// still controlling the storage boundary, such as Azurite-backed integration and E2E tests.
    /// </summary>
    public static ValueTask<RepositoryTestFixture> CreateWithPassphraseAsync(
        IBlobContainerService blobContainer, string accountName, string containerName, string? passphrase = null, 
        LocalDirectory? tempRoot = null)
    {
        const string defaultPassphrase = "arius-test-passphrase";

        var (localRoot, restoreRoot) = CreateTempRoots(tempRoot);
        var (chunkIndexCacheDirectory, fileTreeCacheDirectory, snapshotCacheDirectory) = CreateCacheFolders(accountName, containerName);

        var encryption  = new PassphraseEncryptionService(passphrase ?? defaultPassphrase);
        var compression = TestCompression.Instance;
        var snapshot    = new SnapshotService(blobContainer, encryption, compression, accountName, containerName);
        var index       = new ChunkIndexService(blobContainer, encryption, compression, snapshot, accountName, containerName);

        return ValueTask.FromResult(new RepositoryTestFixture
        {
            BlobContainer                = blobContainer,
            Encryption                   = encryption,
            Compression                  = compression,
            Index                        = index,
            ChunkStorage                 = new ChunkStorageService(blobContainer, encryption, compression),
            FileTreeService              = new FileTreeService(blobContainer, encryption, compression, accountName, containerName),
            Snapshot                     = snapshot,
            ChunkIndexCacheDirectory     = chunkIndexCacheDirectory,
            FileTreeCacheDirectory       = fileTreeCacheDirectory,
            SnapshotCacheDirectory       = snapshotCacheDirectory,
            LocalDirectory               = localRoot,
            RestoreDirectory             = restoreRoot,
            LocalFileSystem              = new RelativeFileSystem(localRoot),
            RestoreFileSystem            = new RelativeFileSystem(restoreRoot),
            AccountName                  = accountName,
            ContainerName                = containerName,
            Mediator                     = Substitute.For<IMediator>()
        });
    }

    /// <summary>
    /// Creates a fixture around a caller-provided blob container and encryption service.
    /// Use this when a test must control encryption behavior explicitly, for example legacy-format
    /// compatibility tests or focused Core tests that seed serialized repository data directly.
    /// </summary>
    public static ValueTask<RepositoryTestFixture> CreateWithEncryptionAsync(
        IBlobContainerService blobContainer, string accountName, string containerName, 
        IEncryptionService encryption, 
        LocalDirectory? tempRoot = null)
    {
        var (localRoot, restoreRoot) = CreateTempRoots(tempRoot);
        var (chunkIndexCacheDirectory, fileTreeCacheDirectory, snapshotCacheDirectory) = CreateCacheFolders(accountName, containerName);

        var compression = TestCompression.Instance;
        var snapshot = new SnapshotService(blobContainer, encryption, compression, accountName, containerName);
        var index    = new ChunkIndexService(blobContainer, encryption, compression, snapshot, accountName, containerName);

        return ValueTask.FromResult(new RepositoryTestFixture
        {
            BlobContainer                = blobContainer,
            Encryption                   = encryption,
            Compression                  = compression,
            Index                        = index,
            ChunkStorage                 = new ChunkStorageService(blobContainer, encryption, compression),
            FileTreeService              = new FileTreeService(blobContainer, encryption, compression, accountName, containerName),
            Snapshot                     = snapshot,
            ChunkIndexCacheDirectory     = chunkIndexCacheDirectory,
            FileTreeCacheDirectory       = fileTreeCacheDirectory,
            SnapshotCacheDirectory       = snapshotCacheDirectory,
            LocalDirectory               = localRoot,
            RestoreDirectory             = restoreRoot,
            LocalFileSystem              = new RelativeFileSystem(localRoot),
            RestoreFileSystem            = new RelativeFileSystem(restoreRoot),
            AccountName                  = accountName,
            ContainerName                = containerName,
            Mediator                     = Substitute.For<IMediator>()
        });
    }

    private static (LocalDirectory SourceRoot, LocalDirectory RestoreDestinationRoot) CreateTempRoots(LocalDirectory? tempRoot = null)
    {
        tempRoot ??= TestTempRoots.CreateDirectory("test");

        var sourceDirectory  = tempRoot.Value / RelativePath.Parse("source");
        var restoreDirectory = tempRoot.Value / RelativePath.Parse("restore");

        var fs = new RelativeFileSystem(tempRoot.Value);

        fs.CreateDirectory(sourceDirectory);
        fs.CreateDirectory(restoreDirectory);

        return (sourceDirectory, restoreDirectory);
    }

    private static (LocalDirectory chunkIndexCacheDirectory, LocalDirectory fileTreeCacheDirectory, LocalDirectory snapshotCacheDirectory) CreateCacheFolders(string accountName, string containerName)
    {
        var chunkIndexCacheDirectory          = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(accountName, containerName);
        var fileTreeCacheDirectory            = RepositoryLocalStatePaths.GetFileTreeCacheRoot(accountName, containerName);
        var snapshotCacheDirectory            = RepositoryLocalStatePaths.GetSnapshotCacheRoot(accountName, containerName);

        var fs = new RelativeFileSystem(RepositoryLocalStatePaths.GetRepositoryRoot(accountName, containerName));
        
        fs.CreateDirectory(chunkIndexCacheDirectory);
        fs.CreateDirectory(fileTreeCacheDirectory);
        fs.CreateDirectory(snapshotCacheDirectory);

        return (chunkIndexCacheDirectory, fileTreeCacheDirectory, snapshotCacheDirectory);
    }

    /// <summary>
    /// Creates a fast unit-test fixture with in-memory storage and shared test encryption.
    /// Use this for Core tests that need a complete repository service graph without Azurite, real
    /// Azure behavior, or passphrase encryption semantics.
    /// </summary>
    public static ValueTask<RepositoryTestFixture> CreateInMemoryAsync(string? accountName = null, string? containerName = null, LocalDirectory? tempRoot = null)
    {
        var blobContainer = new FakeInMemoryBlobContainerService();
        return CreateWithEncryptionAsync(
            blobContainer,
            accountName ?? $"acct-test-{Guid.NewGuid():N}",
            containerName ?? $"ctr-test-{Guid.NewGuid():N}",
            TestEncryption.Instance,
            tempRoot);
    }


    // --- FIELDS ---

    /// <summary>
    /// Storage boundary shared by the repository services in this fixture instance.
    /// This is usually fresh per test even when the underlying backend process is shared.
    /// </summary>
    public required IBlobContainerService BlobContainer { get; init; }

    /// <summary>Encryption service used for repository serialization and chunk payloads.</summary>
    public required IEncryptionService Encryption { get; init; }

    /// <summary>Compression service used for repository serialization and chunk payloads.</summary>
    public required ICompressionService Compression { get; init; }

    /// <summary>Chunk index service used for content-to-chunk lookup and mutation.</summary>
    public required ChunkIndexService Index { get; init; }

    /// <summary>Chunk storage service used by archive and restore handlers.</summary>
    public required IChunkStorageService ChunkStorage { get; init; }

    /// <summary>Filetree service used for reading and writing repository structure.</summary>
    public required FileTreeService FileTreeService { get; init; }

    /// <summary>Snapshot service used for creating, listing, and resolving snapshots.</summary>
    public required SnapshotService Snapshot { get; init; }

    /// <summary>Typed source directory used by archive-oriented tests.</summary>
    internal required LocalDirectory LocalDirectory { get; init; }

    /// <summary>Typed restore destination directory used by restore-oriented tests.</summary>
    public required LocalDirectory RestoreDirectory { get; init; }

    internal required LocalDirectory ChunkIndexCacheDirectory { get; init; }

    internal required LocalDirectory FileTreeCacheDirectory { get; init; }

    internal required LocalDirectory SnapshotCacheDirectory { get; init; }

    /// <summary>Rooted filesystem for the source directory used by archive-oriented tests.</summary>
    internal required RelativeFileSystem LocalFileSystem { get; init; }

    /// <summary>Rooted filesystem for the restore directory used by restore-oriented tests.</summary>
    internal required RelativeFileSystem RestoreFileSystem { get; init; }

    /// <summary>Substitute mediator shared by handler factories so tests can inspect or ignore published events.</summary>
    public required IMediator Mediator { get; init; }

    /// <summary>Repository account name used for cache paths and service wiring.</summary>
    public required string AccountName { get; init; }

    /// <summary>Repository container name used for cache paths and service wiring.</summary>
    public required string ContainerName { get; init; }

    public FakeLogCollector ArchiveLogs => _archiveLogger.Collector;

    public FakeLogCollector RestoreLogs => _restoreLogger.Collector;


    // --- COMMAND HELPERS ---

    /// <summary>Creates an archive handler wired to this fixture's shared repository services.</summary>
    public ArchiveCommandHandler CreateArchiveHandler()
        => new(BlobContainer, Encryption, CreateChunkIndexService(), ChunkStorage, FileTreeService, Snapshot, Mediator, _archiveLogger, NullLoggerFactory.Instance, AccountName, ContainerName);

    internal ArchiveCommandHandler CreateArchiveHandler(Func<LocalDirectory, CancellationToken, Task<IFileTreeStagingSession>> openStagingSession)
        => new(BlobContainer, Encryption, CreateChunkIndexService(), ChunkStorage, FileTreeService, Snapshot, Mediator, _archiveLogger, NullLoggerFactory.Instance, AccountName, ContainerName, openStagingSession);

    /// <summary>Creates a restore handler wired to this fixture's shared repository services.</summary>
    public RestoreCommandHandler CreateRestoreHandler()
        => new(Encryption, CreateChunkIndexService(), ChunkStorage, FileTreeService, Snapshot, Mediator, _restoreLogger, AccountName, ContainerName);

    /// <summary>Creates a list-query handler wired to this fixture's shared repository services.</summary>
    public ListQueryHandler CreateListQueryHandler()
        => new(CreateChunkIndexService(), FileTreeService, Snapshot, _listLogger, AccountName, ContainerName);

    private ChunkIndexService CreateChunkIndexService()
    {
        var index = new ChunkIndexService(BlobContainer, Encryption, Compression, Snapshot, AccountName, ContainerName);
        _ownedIndexes.Add(index);
        return index;
    }


    // --- OTHER HELPERS ---

    /// <summary>
    /// Deletes the local repository cache directory for the supplied account/container pair.
    /// Use this when a test creates repository services directly but still needs standard cache cleanup.
    /// </summary>
    public static void DeleteLocalCacheDirectory(string accountName, string containerName)
    {
        ClearChunkIndexPool(accountName, containerName);
        var repositoryRoot = RepositoryLocalStatePaths.GetRepositoryRoot(accountName, containerName).ToString();
        if (Directory.Exists(repositoryRoot))
            Directory.Delete(repositoryRoot, true);
    }

    public void DeleteLocalCacheDirectory(bool recreate)
    {
        var d = FileTreeCacheDirectory.ToString();
        if (Directory.Exists(d))
            Directory.Delete(d, true);

        if (recreate)
            Directory.CreateDirectory(d);
    }

    public ValueTask DisposeAsync()
    {
        Index.Dispose();
        foreach (var index in _ownedIndexes)
            index.Dispose();

        return ValueTask.CompletedTask;
    }

    private static void ClearChunkIndexPool(string accountName, string containerName)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(accountName, containerName).Resolve(RelativePath.Parse("cache.sqlite")),
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Pooling    = true,
        }.ToString());

        SqliteConnection.ClearPool(connection);
    }
}
