using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Paths;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Fixtures;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging.Testing;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Per-test pipeline fixture: unique Azurite container, pre-wired archive/restore/list query handlers.
/// Uses NSubstitute for IMediator (event capture not needed for most tests).
/// </summary>
public sealed class PipelineFixture : IAsyncDisposable
{
    private readonly RepositoryTestFixture _repository;
    public           BlobContainerClient Container { get; private set; } = null!;

    private readonly FakeLogger<ListQueryHandler> _listLogger = new();

    private const string Account = "devstoreaccount1";

    private PipelineFixture(BlobContainerClient container, RepositoryTestFixture repository)
    {
        Container = container;
        _repository = repository;
    }

    public IBlobContainerService BlobContainer => _repository.BlobContainer;
    public IEncryptionService Encryption => _repository.Encryption;
    public Arius.Core.Shared.ChunkIndex.ChunkIndexService Index => _repository.Index;
    public Arius.Core.Shared.ChunkStorage.IChunkStorageService ChunkStorage => _repository.ChunkStorage;
    public Arius.Core.Shared.FileTree.FileTreeService FileTreeService => _repository.FileTreeService;
    public Arius.Core.Shared.Snapshot.SnapshotService Snapshot => _repository.Snapshot;
    public Mediator.IMediator Mediator => _repository.Mediator;
    public string LocalRoot => _repository.LocalRoot;
    public string RestoreRoot => _repository.RestoreRoot;

    /// <summary>Creates a fully initialised fixture with unique container and temp dirs.</summary>
    public static async Task<PipelineFixture> CreateAsync(
        AzuriteFixture azurite,
        string?        passphrase = null,
        CancellationToken ct = default)
    {
        var (container, svc) = await azurite.CreateTestServiceAsync(ct);
        var repository = await RepositoryTestFixture.CreateWithPassphraseAsync(svc, Account, container.Name, passphrase, cancellationToken: ct);
        return new PipelineFixture(container, repository);
    }

    /// <summary>
    /// Creates a fixture with an explicitly provided encryption service.
    /// Used for tests that need a custom write-path (e.g. legacy CBC simulation).
    /// If <paramref name="existingContainer"/> is supplied, the fixture reuses that
    /// container rather than creating a new one.
    /// </summary>
    public static async Task<PipelineFixture> CreateAsyncWithEncryption(
        AzuriteFixture   azurite,
        IEncryptionService encryption,
        BlobContainerClient? existingContainer = null,
        CancellationToken ct = default)
    {
        BlobContainerClient container;
        IBlobContainerService blobContainer;

        if (existingContainer is not null)
        {
            container = existingContainer;
            blobContainer = azurite.CreateTestServiceFromExistingContainer(existingContainer);
        }
        else
        {
            var created = await azurite.CreateTestServiceAsync(ct);
            container = created.Container;
            blobContainer = created.Service;
        }

        var repository = await RepositoryTestFixture.CreateWithEncryptionAsync(blobContainer, Account, container.Name, encryption, cancellationToken: ct);

        return new PipelineFixture(container, repository);
    }

    // ── Pipeline helpers ──────────────────────────────────────────────────────

    public ArchiveCommandHandler CreateArchiveHandler() =>
        _repository.CreateArchiveHandler();

    public RestoreCommandHandler CreateRestoreHandler() =>
        _repository.CreateRestoreHandler();

    public ListQueryHandler CreateListQueryHandler() =>
        new(Index, FileTreeService, Snapshot,
            _listLogger,
            Account, Container.Name);

    /// <summary>
    /// Runs the archive pipeline against <see cref="LocalRoot"/> with Hot tier
    /// (so restore works without rehydration in tests).
    /// </summary>
    public Task<ArchiveResult> ArchiveAsync(
        ArchiveCommandOptions? opts = null,
        CancellationToken ct = default)
    {
        opts ??= new ArchiveCommandOptions
        {
            RootDirectory = RootOf(LocalRoot),
            UploadTier    = BlobTier.Hot,
        };
        return CreateArchiveHandler().Handle(new ArchiveCommand(opts), ct).AsTask();
    }

    /// <summary>Runs the restore pipeline into <see cref="RestoreRoot"/>.</summary>
    public Task<RestoreResult> RestoreAsync(
        RestoreOptions? opts = null,
        CancellationToken ct = default)
    {
        opts ??= new RestoreOptions
        {
            RootDirectory = RootOf(RestoreRoot),
            Overwrite     = true,
        };
        return CreateRestoreHandler().Handle(new RestoreCommand(opts), ct).AsTask();
    }

    /// <summary>Runs the list query and collects all file entries.</summary>
    public async Task<List<RepositoryFileEntry>> ListAsync(
        ListQueryOptions? opts = null,
        CancellationToken ct = default)
    {
        opts ??= new ListQueryOptions();
        var results = new List<RepositoryFileEntry>();
        await foreach (var entry in CreateListQueryHandler().Handle(new ListQuery(opts), ct))
        {
            if (entry is RepositoryFileEntry file)
                results.Add(file);
        }
        return results;
    }

    // ── File helpers ──────────────────────────────────────────────────────────

    /// <summary>Creates a file under <see cref="LocalRoot"/> with the given content.</summary>
    public string WriteFile(RelativePath relativePath, byte[] content)
        => _repository.WriteFile(relativePath, content);

    /// <summary>Creates a file under <see cref="LocalRoot"/> with random byte content.</summary>
    public string WriteRandomFile(RelativePath relativePath, int sizeBytes)
    {
        var bytes = new byte[sizeBytes];
        Random.Shared.NextBytes(bytes);
        return WriteFile(relativePath, bytes);
    }

    /// <summary>Reads a restored file's bytes from <see cref="RestoreRoot"/>.</summary>
    public byte[] ReadRestored(RelativePath relativePath)
        => _repository.ReadRestored(relativePath);

    /// <summary>Checks whether a restored file exists.</summary>
    public bool RestoredExists(RelativePath relativePath) =>
        _repository.RestoredExists(relativePath);

    /// <summary>
    /// Releases resources used by the fixture by removing the fixture's temporary directory and any repository-specific chunk-index cache directory under the current user's profile, if they exist.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Clean up any cache dirs created by this test's container (unique name)
        var cacheDir = RepositoryPaths.GetRepositoryDirectory(Account, Container.Name);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        await _repository.DisposeAsync();
    }
}
