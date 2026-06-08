using Arius.AzureBlob;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Azure.Storage.Blobs;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Verifies that the archive pipeline auto-creates the blob container when it does not exist,
/// and that it handles the case where the container already exists (idempotent).
///
/// Covers container-creation tasks 3.1 and 3.2.
/// </summary>
[ClassDataSource<AzuriteFixture>(Shared = SharedType.PerTestSession)]
public class ContainerCreationTests(AzuriteFixture azurite)
{
    private const string Account = "devstoreaccount1";

    // ── 3.1: Archive to a non-existent container succeeds (auto-created) ─────

    [Test]
    public async Task Archive_NonExistentContainer_AutoCreatesContainerAndSucceeds()
    {
        // Create a container client pointing at a name that has NOT been created yet
        var containerName   = $"test-new-{Guid.NewGuid():N}";
        var containerClient = new BlobServiceClient(azurite.ConnectionString)
            .GetBlobContainerClient(containerName);

        // Verify the container does not yet exist
        (await containerClient.ExistsAsync()).Value.ShouldBeFalse();

        var svc        = new AzureBlobContainerService(containerClient);
        var encryption = new PlaintextPassthroughService();
        var snapshot   = new SnapshotService(svc, encryption, Account, containerName);
        var index      = new ChunkIndexService(svc, encryption, snapshot, Account, containerName);
        var mediator   = Substitute.For<IMediator>();
        var logger     = new FakeLogger<ArchiveCommandHandler>();
        var handler    = new ArchiveCommandHandler(
            svc, encryption, index, new ChunkStorageService(svc, encryption), new FileTreeService(svc, encryption, Account, containerName),
            snapshot, mediator,
            logger,
            NullLoggerFactory.Instance,
            Account, containerName);

        var tempRoot = TestTempRoots.CreateDirectory("cc");
        var localFileSystem = new RelativeFileSystem(tempRoot);
        localFileSystem.CreateDirectory(RelativePath.Root);
        try
        {
            await localFileSystem.WriteAllTextAsync(RelativePath.Parse("hello.txt"), "hello", CancellationToken.None);

            var opts   = new ArchiveCommandOptions { RootDirectory = tempRoot.ToString(), UploadTier = BlobTier.Hot };
            var result = await handler.Handle(new ArchiveCommand(opts), CancellationToken.None);

            result.Success.ShouldBeTrue(result.ErrorMessage);
            result.FilesUploaded.ShouldBe(1);
            (await containerClient.ExistsAsync()).Value.ShouldBeTrue();
        }
        finally
        {
            localFileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
        }
    }

    // ── Regression: CLI preflight must not fail on a non-existent container ──
    //
    // The archive verb resolves the container through AzureBlobService.OpenContainerServiceAsync in
    // PreflightMode.ReadWrite *before* the handler runs. Previously that probed write access by uploading a
    // blob, which 404s on a missing container -> PreflightException(ContainerNotFound), aborting before the
    // handler's auto-create ever ran. This test exercises the real preflight path against Azurite and asserts
    // it creates the container instead of throwing. The handler-only test above does NOT cover this path.

    [Test]
    public async Task Preflight_ReadWrite_NonExistentContainer_CreatesContainerAndDoesNotThrow()
    {
        var containerName   = $"test-pf-{Guid.NewGuid():N}";
        var serviceClient   = new BlobServiceClient(azurite.ConnectionString);
        var containerClient = serviceClient.GetBlobContainerClient(containerName);

        (await containerClient.ExistsAsync()).Value.ShouldBeFalse();

        var blobService = new AzureBlobService(serviceClient, Account, "key");

        // Before the fix this throws PreflightException(ContainerNotFound).
        var svc = await blobService.OpenContainerServiceAsync(containerName, PreflightMode.ReadWrite, CancellationToken.None);

        svc.ShouldBeOfType<AzureBlobContainerService>();
        (await containerClient.ExistsAsync()).Value.ShouldBeTrue();
    }

    // ── 3.2: Archive to an existing container succeeds (idempotent) ──────────

    [Test]
    public async Task Archive_ExistingContainer_Succeeds_Idempotent()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("hello.txt"), "hello"u8.ToArray(), CancellationToken.None);

        // First archive — container was pre-created by fixture
        var result1 = await fix.ArchiveAsync();
        result1.Success.ShouldBeTrue(result1.ErrorMessage);

        // Second archive — same existing container, CreateContainerIfNotExistsAsync is a no-op
        await fix.LocalFileSystem.WriteAllBytesAsync(RelativePath.Parse("hello2.txt"), "world"u8.ToArray(), CancellationToken.None);
        var result2 = await fix.ArchiveAsync();
        result2.Success.ShouldBeTrue(result2.ErrorMessage);
    }
}
