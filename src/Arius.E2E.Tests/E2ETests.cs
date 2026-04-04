using Arius.AzureBlob;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Storage;
using Azure.Storage.Blobs;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Arius.E2E.Tests;

/// <summary>
/// End-to-end tests against a real Azure Storage account.
///
/// Gated by environment variables:
///   ARIUS_E2E_ACCOUNT  — storage account name
///   ARIUS_E2E_KEY      — storage account key
///
/// Skipped when the env vars are not set.
/// Each test creates and cleans up its own unique container.
///
/// Covers tasks 16.1 – 16.5.
/// </summary>
[ClassDataSource<AzureFixture>(Shared = SharedType.PerTestSession)]
public class E2ETests(AzureFixture azure)
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a pipeline fixture backed by the real Azure container.
    /// The caller is responsible for calling cleanup when done.
    /// </summary>
    private async Task<(E2EFixture Fixture, Func<Task> Cleanup)> CreateFixtureAsync(
        BlobTier tier,
        string?  passphrase = null,
        CancellationToken ct = default)
    {
        var (container, svc, cleanup) = await azure.CreateTestContainerAsync(ct);
        var fix = await E2EFixture.CreateAsync(container, svc, tier, passphrase);
        return (fix, async () =>
        {
            await fix.DisposeAsync();
            await cleanup();
        });
    }

    // ── 16.1: Configuration is set up (implicit — if env vars absent, skip) ──

    [Test]
    public async Task E2E_Configuration_IsAvailable_WhenEnvVarsSet()
    {
        Skip.Unless(AzureFixture.IsAvailable, "ARIUS_E2E_ACCOUNT / ARIUS_E2E_KEY not set — skipping E2E tests");

        AzureFixture.AccountName.ShouldNotBeNullOrWhiteSpace();
        AzureFixture.AccountKey.ShouldNotBeNullOrWhiteSpace();

        // Create and immediately clean up a container to validate credentials work
        var (container, _, cleanup) = await azure.CreateTestContainerAsync();
        try
        {
            var exists = await container.ExistsAsync();
            exists.Value.ShouldBeTrue("Container should have been created");
        }
        finally { await cleanup(); }
    }

    // ── 16.2: Archive to Hot tier → restore → verify content ─────────────────

    [Test]
    public async Task E2E_HotTier_Archive_Restore_ByteIdentical()
    {
        Skip.Unless(AzureFixture.IsAvailable, "ARIUS_E2E_ACCOUNT / ARIUS_E2E_KEY not set — skipping E2E tests");

        var (fix, cleanup) = await CreateFixtureAsync(BlobTier.Hot);
        try
        {
            var content = new byte[1024]; Random.Shared.NextBytes(content);
            fix.WriteFile("hot.bin", content);

            var archiveResult = await fix.ArchiveAsync();
            archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
            archiveResult.FilesUploaded.ShouldBe(1);

            var restoreResult = await fix.RestoreAsync();
            restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
            restoreResult.FilesRestored.ShouldBe(1);

            fix.ReadRestored("hot.bin").ShouldBe(content);
        }
        finally { await cleanup(); }
    }

    // ── 16.3: Archive to Cool tier → restore → verify content ────────────────

    [Test]
    public async Task E2E_CoolTier_Archive_Restore_ByteIdentical()
    {
        Skip.Unless(AzureFixture.IsAvailable, "ARIUS_E2E_ACCOUNT / ARIUS_E2E_KEY not set — skipping E2E tests");

        var (fix, cleanup) = await CreateFixtureAsync(BlobTier.Cool);
        try
        {
            var content = new byte[512]; Random.Shared.NextBytes(content);
            fix.WriteFile("cool.bin", content);

            var archiveResult = await fix.ArchiveAsync();
            archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

            var restoreResult = await fix.RestoreAsync();
            restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);

            fix.ReadRestored("cool.bin").ShouldBe(content);
        }
        finally { await cleanup(); }
    }

    // ── 16.4: Archive to Archive tier → verify blob tier is set ──────────────

    [Test]
    public async Task E2E_ArchiveTier_BlobTierIsSet()
    {
        Skip.Unless(AzureFixture.IsAvailable, "ARIUS_E2E_ACCOUNT / ARIUS_E2E_KEY not set — skipping E2E tests");

        var (fix, cleanup) = await CreateFixtureAsync(BlobTier.Archive);
        try
        {
            var content = new byte[256]; Random.Shared.NextBytes(content);
            fix.WriteFile("archival.bin", content);

            var archiveResult = await fix.ArchiveAsync();
            archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);

            // Verify at least one chunk blob has Archive tier
            var foundArchiveTierBlob = false;
            await foreach (var blobName in fix.BlobContainer.ListAsync(BlobPaths.Chunks))
            {
                var meta = await fix.BlobContainer.GetMetadataAsync(blobName);
                if (meta.Tier == BlobTier.Archive)
                {
                    foundArchiveTierBlob = true;
                    break;
                }
            }
            foundArchiveTierBlob.ShouldBeTrue("Expected at least one chunk blob with Archive tier");
        }
        finally { await cleanup(); }
    }

    // ── 16.5: Large file (100 MB+) upload/download streaming ──────────────────

    [Test]
    [Timeout(300_000)] // 5 minute timeout for large file upload
    public async Task E2E_LargeFile_100MB_Streaming(CancellationToken ct)
    {
        Skip.Unless(AzureFixture.IsAvailable, "ARIUS_E2E_ACCOUNT / ARIUS_E2E_KEY not set — skipping E2E tests");

        var (fix, cleanup) = await CreateFixtureAsync(BlobTier.Hot, ct: ct);
        try
        {
            // 100 MB file → well above threshold → large pipeline
            var content = new byte[100 * 1024 * 1024];
            Random.Shared.NextBytes(content);
            fix.WriteFile("large100mb.bin", content);

            var archiveResult = await fix.ArchiveAsync(ct);
            archiveResult.Success.ShouldBeTrue(archiveResult.ErrorMessage);
            archiveResult.FilesUploaded.ShouldBe(1);

            var restoreResult = await fix.RestoreAsync(ct);
            restoreResult.Success.ShouldBeTrue(restoreResult.ErrorMessage);
            restoreResult.FilesRestored.ShouldBe(1);

            fix.ReadRestored("large100mb.bin").ShouldBe(content);
        }
        finally { await cleanup(); }
    }
}

/// <summary>
/// Lightweight pipeline fixture for E2E tests backed by real Azure.
/// Mirrors the structure of PipelineFixture in integration tests.
/// </summary>
public sealed class E2EFixture : IAsyncDisposable
{
    private readonly string               _tempRoot;
    private readonly BlobTier             _defaultTier;

    public IBlobContainerService BlobContainer  { get; }
    public IEncryptionService  Encryption   { get; }
    public ChunkIndexService   Index        { get; }
    public string              LocalRoot    { get; }
    public string              RestoreRoot  { get; }

    private readonly string   _account;
    private readonly string   _container;
    private readonly IMediator _mediator;

    private E2EFixture(
        IBlobContainerService blobContainer,
        IEncryptionService  encryption,
        ChunkIndexService   index,
        string              tempRoot,
        string              localRoot,
        string              restoreRoot,
        string              account,
        string              containerName,
        BlobTier            defaultTier)
    {
        BlobContainer  = blobContainer;
        Encryption   = encryption;
        Index        = index;
        _tempRoot    = tempRoot;
        LocalRoot    = localRoot;
        RestoreRoot  = restoreRoot;
        _account     = account;
        _container   = containerName;
        _defaultTier = defaultTier;
        _mediator    = Substitute.For<IMediator>();
    }

    public static async Task<E2EFixture> CreateAsync(
        BlobContainerClient   container,
        AzureBlobContainerService svc,
        BlobTier              defaultTier,
        string?               passphrase = null,
        CancellationToken     ct = default)
    {
        var tempRoot    = Path.Combine(Path.GetTempPath(), $"arius-e2e-{Guid.NewGuid():N}");
        var localRoot   = Path.Combine(tempRoot, "source");
        var restoreRoot = Path.Combine(tempRoot, "restore");
        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(restoreRoot);

        var encryption = passphrase is not null
            ? (IEncryptionService)new PassphraseEncryptionService(passphrase)
            : new PlaintextPassthroughService();
        var account    = container.AccountName;
        var index      = new ChunkIndexService(svc, encryption, account, container.Name);

        return new E2EFixture(svc, encryption, index, tempRoot, localRoot, restoreRoot,
                              account, container.Name, defaultTier);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public string WriteFile(string relativePath, byte[] content)
    {
        var full = Path.Combine(LocalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public byte[] ReadRestored(string relativePath)
        => File.ReadAllBytes(
            Path.Combine(RestoreRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public bool RestoredExists(string relativePath)
        => File.Exists(
            Path.Combine(RestoreRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private ArchiveCommandHandler CreateArchiveHandler() =>
        new(BlobContainer, Encryption, Index, new TreeCacheService(BlobContainer, Encryption, _account, _container), _mediator,
            NullLogger<ArchiveCommandHandler>.Instance,
            _account, _container);

    private RestoreCommandHandler CreateRestoreHandler() =>
        new(BlobContainer, Encryption, Index, new TreeCacheService(BlobContainer, Encryption, _account, _container), _mediator,
            NullLogger<RestoreCommandHandler>.Instance,
            _account, _container);

    public Task<ArchiveResult> ArchiveAsync(CancellationToken ct = default) =>
        CreateArchiveHandler().Handle(
            new ArchiveCommand(new ArchiveCommandOptions
            {
                RootDirectory = LocalRoot,
                UploadTier    = _defaultTier,
            }), ct).AsTask();

    public Task<RestoreResult> RestoreAsync(CancellationToken ct = default) =>
        CreateRestoreHandler().Handle(
            new RestoreCommand(new RestoreOptions
            {
                RootDirectory = RestoreRoot,
                Overwrite     = true,
            }), ct).AsTask();

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);

        var home     = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cacheDir = Path.Combine(home, ".arius", ChunkIndexService.GetRepoDirectoryName(_account, _container));
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);

        await Task.CompletedTask;
    }
}
