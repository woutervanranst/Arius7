using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Arius.AzureBlob.Tests.Fakes;

public sealed class FakeBlobServiceClient(IReadOnlyList<FakeContainer> containers) : BlobServiceClient
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