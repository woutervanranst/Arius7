using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.Restore;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;
using Arius.Integration.Tests.Storage;
using Azure.Storage.Blobs;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Per-test pipeline fixture: unique Azurite container, pre-wired archive/restore/ls handlers.
/// Uses NSubstitute for IMediator (event capture not needed for most tests).
/// </summary>
public sealed class PipelineFixture : IAsyncDisposable
{
    private readonly AzuriteFixture  _azurite;
    private readonly string          _tempRoot;
    public           BlobContainerClient Container { get; private set; } = null!;

    public  IBlobContainerService BlobContainer   { get; private set; } = null!;
    public  IEncryptionService  Encryption    { get; private set; } = null!;
    public  ChunkIndexService   Index         { get; private set; } = null!;
    public  IMediator           Mediator      { get; private set; } = null!;

    public  string              LocalRoot     { get; private set; } = null!;
    public  string              RestoreRoot   { get; private set; } = null!;

    private const string Account = "devstoreaccount1";

    private PipelineFixture(AzuriteFixture azurite, string tempRoot)
    {
        _azurite  = azurite;
        _tempRoot = tempRoot;
    }

    /// <summary>Creates a fully initialised fixture with unique container and temp dirs.</summary>
    public static async Task<PipelineFixture> CreateAsync(
        AzuriteFixture azurite,
        string?        passphrase = null,
        CancellationToken ct = default)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-pipe-{Guid.NewGuid():N}");
        var fixture = new PipelineFixture(azurite, tempRoot);
        await fixture.InitAsync(passphrase, ct);
        return fixture;
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
        var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-pipe-{Guid.NewGuid():N}");
        var fixture  = new PipelineFixture(azurite, tempRoot);
        await fixture.InitAsyncWithEncryption(encryption, existingContainer, ct);
        return fixture;
    }

    private async Task InitAsync(string? passphrase, CancellationToken ct)
    {
        var (container, svc) = await _azurite.CreateTestServiceAsync(ct);
        Container   = container;
        BlobContainer = svc;
        Encryption  = passphrase is not null
            ? new PassphraseEncryptionService(passphrase)
            : new PlaintextPassthroughService();
        Index      = new ChunkIndexService(BlobContainer, Encryption, Account, container.Name);
        Mediator   = Substitute.For<IMediator>();

        LocalRoot   = Path.Combine(_tempRoot, "source");
        RestoreRoot = Path.Combine(_tempRoot, "restore");
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateDirectory(RestoreRoot);
    }

    private async Task InitAsyncWithEncryption(
        IEncryptionService   encryption,
        BlobContainerClient? existingContainer,
        CancellationToken    ct)
    {
        if (existingContainer is not null)
        {
            Container   = existingContainer;
            BlobContainer = _azurite.CreateTestServiceFromExistingContainer(existingContainer);
        }
        else
        {
            var (container, svc) = await _azurite.CreateTestServiceAsync(ct);
            Container   = container;
            BlobContainer = svc;
        }

        Encryption = encryption;
        Index      = new ChunkIndexService(BlobContainer, Encryption, Account, Container.Name);
        Mediator   = Substitute.For<IMediator>();

        LocalRoot   = Path.Combine(_tempRoot, "source");
        RestoreRoot = Path.Combine(_tempRoot, "restore");
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateDirectory(RestoreRoot);
    }

    // ── Pipeline helpers ──────────────────────────────────────────────────────

    public ArchiveCommandHandler CreateArchiveHandler() =>
        new(BlobContainer, Encryption, Index, Mediator,
            NullLogger<ArchiveCommandHandler>.Instance,
            Account, Container.Name);

    public RestoreCommandHandler CreateRestoreHandler() =>
        new(BlobContainer, Encryption, Index, Mediator,
            NullLogger<RestoreCommandHandler>.Instance,
            Account, Container.Name);

    public ListQueryHandler CreateLsHandler() =>
        new(BlobContainer, Encryption, Index,
            NullLogger<ListQueryHandler>.Instance,
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
            RootDirectory = LocalRoot,
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
            RootDirectory = RestoreRoot,
            Overwrite     = true,
        };
        return CreateRestoreHandler().Handle(new RestoreCommand(opts), ct).AsTask();
    }

    /// <summary>Runs the ls command and collects all file entries.</summary>
    public async Task<List<RepositoryFileEntry>> LsAsync(
        ListQueryOptions? opts = null,
        CancellationToken ct = default)
    {
        opts ??= new ListQueryOptions();
        var results = new List<RepositoryFileEntry>();
        await foreach (var entry in CreateLsHandler().Handle(new ListQuery(opts), ct))
        {
            if (entry is RepositoryFileEntry file)
                results.Add(file);
        }
        return results;
    }

    // ── File helpers ──────────────────────────────────────────────────────────

    /// <summary>Creates a file under <see cref="LocalRoot"/> with the given content.</summary>
    public string WriteFile(string relativePath, byte[] content)
    {
        var full = Path.Combine(LocalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    /// <summary>Creates a file under <see cref="LocalRoot"/> with random byte content.</summary>
    public string WriteRandomFile(string relativePath, int sizeBytes)
    {
        var bytes = new byte[sizeBytes];
        Random.Shared.NextBytes(bytes);
        return WriteFile(relativePath, bytes);
    }

    /// <summary>Reads a restored file's bytes from <see cref="RestoreRoot"/>.</summary>
    public byte[] ReadRestored(string relativePath)
    {
        var full = Path.Combine(RestoreRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.ReadAllBytes(full);
    }

    /// <summary>Checks whether a restored file exists.</summary>
    public bool RestoredExists(string relativePath) =>
        File.Exists(Path.Combine(RestoreRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Releases resources used by the fixture by removing the fixture's temporary directory and any repository-specific chunk-index cache directory under the current user's profile, if they exist.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Clean up unique temp dir
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);

        // Clean up any cache dirs created by this test's container (unique name)
        var home     = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cacheDir = Path.Combine(home, ".arius", ChunkIndexService.GetRepoDirectoryName(Account, Container.Name));
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        await Task.CompletedTask;
    }
}
