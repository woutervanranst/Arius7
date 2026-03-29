using Arius.Core.Archive;
using Arius.Core.ChunkIndex;
using Arius.Core.Encryption;
using Arius.Core.Ls;
using Arius.Core.Restore;
using Arius.Core.Storage;
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

    public  IBlobStorageService BlobStorage   { get; private set; } = null!;
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

    private async Task InitAsync(string? passphrase, CancellationToken ct)
    {
        var (container, svc) = await _azurite.CreateTestServiceAsync(ct);
        Container   = container;
        BlobStorage = svc;
        Encryption  = passphrase is not null
            ? new PassphraseEncryptionService(passphrase)
            : new PlaintextPassthroughService();
        Index      = new ChunkIndexService(BlobStorage, Encryption, Account, container.Name);
        Mediator   = Substitute.For<IMediator>();

        LocalRoot   = Path.Combine(_tempRoot, "source");
        RestoreRoot = Path.Combine(_tempRoot, "restore");
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateDirectory(RestoreRoot);
    }

    // ── Pipeline helpers ──────────────────────────────────────────────────────

    public ArchivePipelineHandler CreateArchiveHandler() =>
        new(BlobStorage, Encryption, Index, Mediator,
            NullLogger<ArchivePipelineHandler>.Instance,
            Account, Container.Name);

    public RestorePipelineHandler CreateRestoreHandler() =>
        new(BlobStorage, Encryption, Index, Mediator,
            NullLogger<RestorePipelineHandler>.Instance,
            Account, Container.Name);

    public LsHandler CreateLsHandler() =>
        new(BlobStorage, Encryption, Index,
            NullLogger<LsHandler>.Instance,
            Account, Container.Name);

    /// <summary>
    /// Runs the archive pipeline against <see cref="LocalRoot"/> with Hot tier
    /// (so restore works without rehydration in tests).
    /// </summary>
    public Task<ArchiveResult> ArchiveAsync(
        ArchiveOptions? opts = null,
        CancellationToken ct = default)
    {
        opts ??= new ArchiveOptions
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

    /// <summary>Runs the ls command.</summary>
    public Task<LsResult> LsAsync(
        LsOptions? opts = null,
        CancellationToken ct = default)
    {
        opts ??= new LsOptions();
        return CreateLsHandler().Handle(new LsCommand(opts), ct).AsTask();
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
