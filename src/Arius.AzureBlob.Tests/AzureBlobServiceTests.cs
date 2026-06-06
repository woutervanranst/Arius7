using Arius.Core.Shared.Storage;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

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
    public async Task GetContainerServiceAsync_ReadOnly_ReturnsContainerServiceWhenContainerExists()
    {
        var service = new AzureBlobService(
            new FakeBlobServiceClient([new FakeContainer("repo-a", exists: true, [])]),
            "account",
            "key");

        var containerService = await service.GetContainerServiceAsync("repo-a", PreflightMode.ReadOnly, CancellationToken.None);

        containerService.ShouldBeOfType<AzureBlobContainerService>();
    }

    [Test]
    public async Task GetContainerServiceAsync_ReadOnly_ThrowsWhenContainerIsMissing()
    {
        var service = new AzureBlobService(
            new FakeBlobServiceClient([new FakeContainer("repo-a", exists: false, [])]),
            "account",
            "key");

        var ex = await Should.ThrowAsync<PreflightException>(() => service.GetContainerServiceAsync("repo-a", PreflightMode.ReadOnly, CancellationToken.None));

        ex.ErrorKind.ShouldBe(PreflightErrorKind.ContainerNotFound);
        ex.ContainerName.ShouldBe("repo-a");
    }

    [Test]
    public async Task GetContainerServiceAsync_ReadWrite_ProbesUploadAndDelete()
    {
        var container = new FakeContainer("repo-a", exists: true, []);
        var service = new AzureBlobService(
            new FakeBlobServiceClient([container]),
            "account",
            "key");

        var containerService = await service.GetContainerServiceAsync("repo-a", PreflightMode.ReadWrite, CancellationToken.None);

        containerService.ShouldBeOfType<AzureBlobContainerService>();
        container.UploadedProbe.ShouldBeTrue();
        container.DeletedProbe.ShouldBeTrue();
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

        var containerService = await service.GetContainerServiceAsync("repo-a", PreflightMode.ReadOnly, CancellationToken.None);

        var results = new List<string>();
        await foreach (var item in containerService.ListAsync(BlobPaths.SnapshotsPrefix, cancellationToken: CancellationToken.None))
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

        var containerService = await service.GetContainerServiceAsync("repo-a", PreflightMode.ReadOnly, CancellationToken.None);

        var results = new List<string>();
        await foreach (var item in containerService.ListAsync(BlobPaths.ChunksPrefix, cancellationToken: CancellationToken.None))
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

        var containerService = await service.GetContainerServiceAsync("repo-a", PreflightMode.ReadOnly, CancellationToken.None);

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

        var containerService = await service.GetContainerServiceAsync("repo-a", PreflightMode.ReadOnly, CancellationToken.None);

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

        var containerService = await service.GetContainerServiceAsync("repo-a", PreflightMode.ReadOnly, CancellationToken.None);

        var results = new List<BlobListItem>();
        await foreach (var item in containerService.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: true, cancellationToken: CancellationToken.None))
        {
            results.Add(item);
        }

        results.ShouldHaveSingleItem();
        results[0].ETag.ShouldBe("\"etag-list\"");
    }

    private sealed class FakeBlobServiceClient(IReadOnlyList<FakeContainer> containers) : BlobServiceClient
    {
        public override AsyncPageable<BlobContainerItem> GetBlobContainersAsync(
            BlobContainerTraits traits = BlobContainerTraits.None,
            BlobContainerStates states = BlobContainerStates.None,
            string prefix = default!,
            CancellationToken cancellationToken = default)
        {
            var items = containers
                .Where(container => string.IsNullOrEmpty(prefix) || container.Name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(container => BlobsModelFactory.BlobContainerItem(
                    container.Name,
                    BlobsModelFactory.BlobContainerProperties(
                        DateTimeOffset.UtcNow,
                        default,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        new Dictionary<string, string>())))
                .ToArray();

            return AsyncPageable<BlobContainerItem>.FromPages([Page<BlobContainerItem>.FromValues(items, continuationToken: null, FakeResponse.Instance)]);
        }

        public override BlobContainerClient GetBlobContainerClient(string blobContainerName)
        {
            var container = containers.Single(container => container.Name == blobContainerName);
            return new FakeBlobContainerClient(container);
        }
    }

    private sealed class FakeBlobContainerClient(FakeContainer container) : BlobContainerClient
    {
        public override Task<Response<bool>> ExistsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Response<bool>>(Response.FromValue(container.Exists, FakeResponse.Instance));

        public override AsyncPageable<BlobHierarchyItem> GetBlobsByHierarchyAsync(
            BlobTraits traits = BlobTraits.None,
            BlobStates states = BlobStates.None,
            string delimiter = default!,
            string prefix = default!,
            CancellationToken cancellationToken = default)
        {
            var items = new List<BlobHierarchyItem>();
            var prefixes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in container.BlobNames.Where(name => string.IsNullOrEmpty(prefix) || name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var remaining = string.IsNullOrEmpty(prefix) ? name : name[prefix.Length..];
                var delimiterIndex = string.IsNullOrEmpty(delimiter)
                    ? -1
                    : remaining.IndexOf(delimiter, StringComparison.Ordinal);

                if (delimiterIndex >= 0)
                {
                    var hierarchyPrefix = name[..(name.Length - remaining.Length + delimiterIndex + delimiter.Length)];
                    if (prefixes.Add(hierarchyPrefix))
                    {
                        items.Add(BlobsModelFactory.BlobHierarchyItem(prefix: hierarchyPrefix, blob: null));
                    }

                    continue;
                }

                items.Add(BlobsModelFactory.BlobHierarchyItem(prefix: null, blob: BlobsModelFactory.BlobItem(
                    name,
                    false,
                    BlobsModelFactory.BlobItemProperties(
                        accessTierInferred: true,
                        copySource: new Uri("https://example.test/blob"),
                        contentLength: 1L,
                        accessTier: AccessTier.Cool,
                        eTag: container.ListEtag),
                    null,
                    new Dictionary<string, string>())));
            }

            return AsyncPageable<BlobHierarchyItem>.FromPages([Page<BlobHierarchyItem>.FromValues(items, continuationToken: null, FakeResponse.Instance)]);
        }

        public override AsyncPageable<BlobItem> GetBlobsAsync(
            BlobTraits traits = BlobTraits.None,
            BlobStates states = BlobStates.None,
            string prefix = default!,
            CancellationToken cancellationToken = default)
        {
            if (!container.Exists)
            {
                throw new RequestFailedException(404, "Container not found");
            }

            var items = container.BlobNames
                .Where(name => string.IsNullOrEmpty(prefix) || name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(name => BlobsModelFactory.BlobItem(
                    name,
                    false,
                    BlobsModelFactory.BlobItemProperties(
                        accessTierInferred: true,
                        copySource: new Uri("https://example.test/blob"),
                        contentLength: 1L,
                        accessTier: AccessTier.Cool,
                        eTag: container.ListEtag),
                    null,
                    new Dictionary<string, string>()))
                .ToArray();

            return AsyncPageable<BlobItem>.FromPages([Page<BlobItem>.FromValues(items, continuationToken: null, FakeResponse.Instance)]);
        }

        public override BlobClient GetBlobClient(string blobName) => new FakeBlobClient(container, blobName);
    }

    private sealed class FakeBlobClient(FakeContainer container, string blobName) : BlobClient
    {
        public override Task<Response<BlobContentInfo>> UploadAsync(Stream content, BlobUploadOptions options, CancellationToken cancellationToken = default)
        {
            if (blobName == ".arius-preflight-probe")
            {
                container.UploadedProbe = true;
            }

            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobContentInfo(new ETag("\"etag-upload\""), default, default, default, default, default, default),
                FakeResponse.Instance));
        }

        public override Task<Response<BlobContentInfo>> UploadAsync(Stream content, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            if (blobName == ".arius-preflight-probe")
            {
                container.UploadedProbe = true;
            }

            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobContentInfo(default, default, default, default, default, default, default),
                FakeResponse.Instance));
        }

        public override Task<Response<BlobProperties>> GetPropertiesAsync(BlobRequestConditions conditions = null!, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobProperties(
                    lastModified: DateTimeOffset.UtcNow,
                    contentLength: 3,
                    accessTier: AccessTier.Cool.ToString(),
                    eTag: container.MetadataEtag,
                    metadata: new Dictionary<string, string>()),
                FakeResponse.Instance));
        }

        public override Task<Response> DeleteAsync(DeleteSnapshotsOption snapshotsOption = DeleteSnapshotsOption.None, BlobRequestConditions conditions = null!, CancellationToken cancellationToken = default)
        {
            if (blobName == ".arius-preflight-probe")
            {
                container.DeletedProbe = true;
            }

            return Task.FromResult<Response>(FakeResponse.Instance);
        }
    }

    private sealed class FakeContainer(string name, bool exists, IReadOnlyList<string> blobNames)
    {
        public string Name { get; } = name;
        public bool Exists { get; } = exists;
        public IReadOnlyList<string> BlobNames { get; } = blobNames;
        public bool UploadedProbe { get; set; }
        public bool DeletedProbe { get; set; }
        public ETag MetadataEtag { get; set; } = default;
        public ETag ListEtag { get; set; } = default;
    }

    private sealed class FakeResponse : Response
    {
        public static FakeResponse Instance { get; } = new();

        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose() { }

        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
        protected override bool TryGetHeader(string name, out string value)
        {
            value = string.Empty;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            values = Array.Empty<string>();
            return false;
        }
    }
}
