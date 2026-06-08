using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Arius.AzureBlob.Tests.Fakes;

public sealed class FakeBlobContainerClient(FakeContainer container) : BlobContainerClient
{
    public override Task<Response<bool>> ExistsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<Response<bool>>(Response.FromValue(container.Exists, FakeResponse.Instance));

    public override Task<Response<BlobContainerInfo>> CreateIfNotExistsAsync(
        PublicAccessType publicAccessType = PublicAccessType.None,
        IDictionary<string, string> metadata = null!,
        BlobContainerEncryptionScopeOptions encryptionScopeOptions = null!,
        CancellationToken cancellationToken = default)
    {
        if (!container.Exists)
        {
            container.Exists           = true;
            container.CreatedContainer = true;
        }

        return Task.FromResult(Response.FromValue(
            BlobsModelFactory.BlobContainerInfo(new ETag("\"etag-container\""), DateTimeOffset.UtcNow),
            FakeResponse.Instance));
    }

    public override AsyncPageable<BlobHierarchyItem> GetBlobsByHierarchyAsync(
        BlobTraits traits = BlobTraits.None,
        BlobStates states = BlobStates.None,
        string delimiter = default!,
        string prefix = default!,
        CancellationToken cancellationToken = default)
    {
        var items    = new List<BlobHierarchyItem>();
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