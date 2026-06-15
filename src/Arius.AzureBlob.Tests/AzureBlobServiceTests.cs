using Arius.AzureBlob.Tests.Fakes;
using Arius.Core.Shared.Storage;
using Azure;
using Azure.Storage.Blobs.Specialized;

namespace Arius.AzureBlob.Tests;

public class AzureBlobServiceTests
{
    [Test]
    public async Task GetContainerNamesAsync_YieldsOnlyContainersWithSnapshotsPrefix()
    {
        var service = new AzureBlobService(
            new FakeBlobServiceClient(
                [
                    new FakeContainer("repo-a", exists: true, ["snapshots/2026-04-01T120000.000Z"]),
                    new FakeContainer("not-a-repo", exists: true, ["random/file.txt"]),
                    new FakeContainer("repo-b", exists: true, ["snapshots/2026-04-02T120000.000Z", "chunks/abc"]),
                    new FakeContainer("empty", exists: true, [])
                ]),
            "account",
            "key");

        var results = new List<string>();
        await foreach (var name in service.GetContainerNamesAsync(CancellationToken.None))
        {
            results.Add(name);
        }

        results.ShouldBe(["repo-a", "repo-b"]);
    }

    [Test]
    public async Task GetContainerNamesAsync_DoesNotYieldContainersWithoutSnapshotBlobs()
    {
        var service = new AzureBlobService(
            new FakeBlobServiceClient(
                [
                    new FakeContainer("not-a-repo", exists: true, ["random/file.txt"]),
                    new FakeContainer("empty", exists: true, []),
                ]),
            "account",
            "key");

        var results = new List<string>();
        await foreach (var name in service.GetContainerNamesAsync(CancellationToken.None))
        {
            results.Add(name);
        }

        results.ShouldBeEmpty();
    }

    [Test]
    public async Task GetContainerNamesAsync_DoesNotTreatBlobNamedSnapshotsAsRepository()
    {
        var service = new AzureBlobService(
            new FakeBlobServiceClient(
                [
                    new FakeContainer("repo-a", exists: true, ["snapshots/2026-04-01T120000.000Z"]),
                    new FakeContainer("snapshot-like", exists: true, ["snapshots"]),
                ]),
            "account",
            "key");

        var results = new List<string>();
        await foreach (var name in service.GetContainerNamesAsync(CancellationToken.None))
        {
            results.Add(name);
        }

        results.ShouldBe(["repo-a"]);
    }

    [Test]
    public async Task GetContainerNamesAsync_DoesNotTreatSnapshotLikeSiblingPrefixAsRepository()
    {
        var service = new AzureBlobService(
            new FakeBlobServiceClient(
                [
                    new FakeContainer("repo-a", exists: true, ["snapshots/2026-04-01T120000.000Z"]),
                    new FakeContainer("snapshot-like", exists: true, ["snapshots-archive/2026-04-01T120000.000Z"]),
                ]),
            "account",
            "key");

        var results = new List<string>();
        await foreach (var name in service.GetContainerNamesAsync(CancellationToken.None))
        {
            results.Add(name);
        }

        results.ShouldBe(["repo-a"]);
    }

    [Test]
    public async Task OpenContainerServiceAsync_ReadOnly_ReturnsContainerServiceWhenContainerExists()
    {
        var service = new AzureBlobService(
            new FakeBlobServiceClient([new FakeContainer("repo-a", exists: true, [])]),
            "account",
            "key");

        var containerService = await service.OpenContainerServiceAsync("repo-a", PreflightMode.ReadOnly, CancellationToken.None);

        containerService.ShouldBeOfType<AzureBlobContainerService>();
    }

    [Test]
    public async Task OpenContainerServiceAsync_ReadOnly_ThrowsWhenContainerIsMissing()
    {
        var service = new AzureBlobService(
            new FakeBlobServiceClient([new FakeContainer("repo-a", exists: false, [])]),
            "account",
            "key");

        var ex = await Should.ThrowAsync<PreflightException>(() => service.OpenContainerServiceAsync("repo-a", PreflightMode.ReadOnly, CancellationToken.None));

        ex.ErrorKind.ShouldBe(PreflightErrorKind.ContainerNotFound);
        ex.ContainerName.ShouldBe("repo-a");
    }

    [Test]
    public async Task OpenContainerServiceAsync_ReadWrite_ProbesUploadAndDelete()
    {
        var container = new FakeContainer("repo-a", exists: true, []);
        var service = new AzureBlobService(
            new FakeBlobServiceClient([container]),
            "account",
            "key");

        var containerService = await service.OpenContainerServiceAsync("repo-a", PreflightMode.ReadWrite, CancellationToken.None);

        containerService.ShouldBeOfType<AzureBlobContainerService>();
        container.UploadedProbe.ShouldBeTrue();
        container.DeletedProbe.ShouldBeTrue();
    }

    [Test]
    public async Task OpenContainerServiceAsync_ReadWrite_CreatesMissingContainerWithoutReprobing()
    {
        // Regression: first-run archive against a non-existent container must auto-create it rather than
        // failing the preflight with ContainerNotFound (the write probe 404s on a missing container).
        var container = new FakeContainer("repo-a", exists: false, []);
        var service = new AzureBlobService(
            new FakeBlobServiceClient([container]),
            "account",
            "key");

        var containerService = await service.OpenContainerServiceAsync("repo-a", PreflightMode.ReadWrite, CancellationToken.None);

        containerService.ShouldBeOfType<AzureBlobContainerService>();
        container.CreatedContainer.ShouldBeTrue();
        // A successful create proves write access, so we don't re-probe (no probe blob is left behind to clean up).
        container.UploadedProbe.ShouldBeFalse();
        container.DeletedProbe.ShouldBeFalse();
    }

    [Test]
    public async Task ListAsync_DoesNotReturnSiblingPrefixMatches()
    {
        var container = new FakeContainer(
            "repo-a",
            exists: true,
            [
                "snapshots/2026-04-01T120000.000Z",
                "snapshots-archive/2026-04-01T120000.000Z",
                "snapshots"
            ]);
        var service = new AzureBlobService(
            new FakeBlobServiceClient([container]),
            "account",
            "key");

        var containerService = await service.OpenContainerServiceAsync("repo-a", PreflightMode.ReadOnly, CancellationToken.None);

        var results = new List<string>();
        await foreach (var item in containerService.ListAsync(BlobPaths.SnapshotsPrefix, includeMetadata: false, cancellationToken: CancellationToken.None))
        {
            results.Add(item.Name.ToString());
        }

        results.ShouldBe(["snapshots/2026-04-01T120000.000Z"]);
    }

    [Test]
    public async Task ListAsync_ChunksPrefix_DoesNotReturnRehydratedChunkMatches()
    {
        var container = new FakeContainer(
            "repo-a",
            exists: true,
            [
                "chunks/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "chunks-rehydrated/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "chunks"
            ]);
        var service = new AzureBlobService(
            new FakeBlobServiceClient([container]),
            "account",
            "key");

        var containerService = await service.OpenContainerServiceAsync("repo-a", PreflightMode.ReadOnly, CancellationToken.None);

        var results = new List<string>();
        await foreach (var item in containerService.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: false, cancellationToken: CancellationToken.None))
        {
            results.Add(item.Name.ToString());
        }

        results.ShouldBe(["chunks/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]);
    }

    [Test]
    public async Task UploadAsync_ReturnsOpaqueBlobIdentityFromAzureEtag()
    {
        var container = new FakeContainer("repo-a", exists: true, []);
        var service = new AzureBlobService(
            new FakeBlobServiceClient([container]),
            "account",
            "key");

        var containerService = await service.OpenContainerServiceAsync("repo-a", PreflightMode.ReadOnly, CancellationToken.None);

        var result = await containerService.UploadAsync(
            RelativePath.Parse("chunks/identity-upload"),
            new MemoryStream([1, 2, 3]),
            new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeLarge },
            BlobTier.Cool,
            cancellationToken: CancellationToken.None);

        result.ETag.ShouldBe("\"etag-upload\"");
    }

    [Test]
    public async Task GetMetadataAsync_ReturnsOpaqueBlobIdentityFromAzureEtag()
    {
        var container = new FakeContainer(
            "repo-a",
            exists: true,
            ["chunks/identity-head"])
        {
            MetadataEtag = new ETag("\"etag-head\"")
        };
        var service = new AzureBlobService(
            new FakeBlobServiceClient([container]),
            "account",
            "key");

        var containerService = await service.OpenContainerServiceAsync("repo-a", PreflightMode.ReadOnly, CancellationToken.None);

        var result = await containerService.GetMetadataAsync(RelativePath.Parse("chunks/identity-head"), CancellationToken.None);

        result.Exists.ShouldBeTrue();
        result.ETag.ShouldBe("\"etag-head\"");
    }

    [Test]
    public async Task ListAsync_WithMetadata_ReturnsOpaqueBlobIdentityFromAzureEtag()
    {
        var container = new FakeContainer(
            "repo-a",
            exists: true,
            ["chunks/identity-list"])
        {
            ListEtag = new ETag("\"etag-list\"")
        };
        var service = new AzureBlobService(
            new FakeBlobServiceClient([container]),
            "account",
            "key");

        var containerService = await service.OpenContainerServiceAsync("repo-a", PreflightMode.ReadOnly, CancellationToken.None);

        var results = new List<BlobListItem>();
        await foreach (var item in containerService.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: true, cancellationToken: CancellationToken.None))
        {
            results.Add(item);
        }

        results.ShouldHaveSingleItem();
        results[0].ETag.ShouldBe("\"etag-list\"");
    }
}
