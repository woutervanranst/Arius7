using Arius.AzureBlob;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;
using Arius.Integration.Tests.Storage;
using Azure.Storage.Blobs;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

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
        var index      = new ChunkIndexService(svc, encryption, Account, containerName);
        var mediator   = Substitute.For<IMediator>();
        var handler    = new ArchiveCommandHandler(
            svc, encryption, index, mediator,
            NullLogger<ArchiveCommandHandler>.Instance,
            Account, containerName);

        var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-cc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "hello.txt"), "hello");

            var opts   = new ArchiveCommandOptions { RootDirectory = tempRoot, UploadTier = BlobTier.Hot };
            var result = await handler.Handle(new ArchiveCommand(opts), CancellationToken.None);

            result.Success.ShouldBeTrue(result.ErrorMessage);
            result.FilesUploaded.ShouldBe(1);
            (await containerClient.ExistsAsync()).Value.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    // ── 3.2: Archive to an existing container succeeds (idempotent) ──────────

    [Test]
    public async Task Archive_ExistingContainer_Succeeds_Idempotent()
    {
        await using var fix = await PipelineFixture.CreateAsync(azurite);
        fix.WriteFile("hello.txt", "hello"u8.ToArray());

        // First archive — container was pre-created by fixture
        var result1 = await fix.ArchiveAsync();
        result1.Success.ShouldBeTrue(result1.ErrorMessage);

        // Second archive — same existing container, CreateContainerIfNotExistsAsync is a no-op
        fix.WriteFile("hello2.txt", "world"u8.ToArray());
        var result2 = await fix.ArchiveAsync();
        result2.Success.ShouldBeTrue(result2.ErrorMessage);
    }
}
