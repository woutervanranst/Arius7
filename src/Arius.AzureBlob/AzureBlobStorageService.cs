using Arius.Core.Storage;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CoreRehydratePriority = Arius.Core.Storage.RehydratePriority;
using AzureRehydratePriority = Azure.Storage.Blobs.Models.RehydratePriority;

namespace Arius.AzureBlob;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IBlobStorageService"/>.
/// Operates against a single Azure Blob container.
/// </summary>
public sealed class AzureBlobStorageService : IBlobStorageService
{
    private readonly BlobContainerClient _container;

    public AzureBlobStorageService(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    public async Task UploadAsync(
        string                              blobName,
        Stream                              content,
        IReadOnlyDictionary<string, string> metadata,
        BlobTier                            tier,
        bool                                overwrite         = false,
        CancellationToken                   cancellationToken = default)
    {
        var blobClient = _container.GetBlobClient(blobName);

        var uploadOptions = new BlobUploadOptions
        {
            Metadata    = new Dictionary<string, string>(metadata),
            AccessTier  = ToAzureTier(tier),
            Conditions  = overwrite ? null : new BlobRequestConditions { IfNoneMatch = ETag.All }
        };

        await blobClient.UploadAsync(content, uploadOptions, cancellationToken);
    }

    // ── Download ──────────────────────────────────────────────────────────────

    public async Task<Stream> DownloadAsync(
        string            blobName,
        CancellationToken cancellationToken = default)
    {
        var blobClient = _container.GetBlobClient(blobName);
        var response   = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
        return response.Value.Content;
    }

    // ── HEAD ──────────────────────────────────────────────────────────────────

    public async Task<BlobMetadata> GetMetadataAsync(
        string            blobName,
        CancellationToken cancellationToken = default)
    {
        var blobClient = _container.GetBlobClient(blobName);
        try
        {
            var props = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken);
            var p     = props.Value;

            return new BlobMetadata
            {
                Exists        = true,
                Tier          = FromAzureTier(p.AccessTier),
                ContentLength = p.ContentLength,
                IsRehydrating = p.ArchiveStatus is "rehydrate-pending-to-hot" or "rehydrate-pending-to-cool",
                Metadata      = (IReadOnlyDictionary<string, string>)p.Metadata
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new BlobMetadata { Exists = false };
        }
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<string> ListAsync(
        string            prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (var item in _container.GetBlobsAsync(
                           traits: BlobTraits.None,
                           states: BlobStates.None,
                           prefix: prefix,
                           cancellationToken: cancellationToken))
            yield return item.Name;
    }

    // ── Metadata update ───────────────────────────────────────────────────────

    public async Task SetMetadataAsync(
        string                              blobName,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken                   cancellationToken = default)
    {
        var blobClient = _container.GetBlobClient(blobName);
        await blobClient.SetMetadataAsync(
            new Dictionary<string, string>(metadata),
            cancellationToken: cancellationToken);
    }

    // ── Copy (rehydration) ────────────────────────────────────────────────────

    public async Task CopyAsync(
        string                    sourceBlobName,
        string                    destinationBlobName,
        BlobTier                  destinationTier,
        CoreRehydratePriority?    rehydratePriority = null,
        CancellationToken         cancellationToken = default)
    {
        var sourceUri = _container.GetBlobClient(sourceBlobName).Uri;
        var destBlob  = _container.GetBlobClient(destinationBlobName);

        var copyOptions = new BlobCopyFromUriOptions
        {
            AccessTier        = ToAzureTier(destinationTier),
            RehydratePriority = rehydratePriority.HasValue
                                    ? ToAzureRehydratePriority(rehydratePriority.Value)
                                    : null
        };

        await destBlob.StartCopyFromUriAsync(sourceUri, copyOptions, cancellationToken);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task DeleteAsync(
        string            blobName,
        CancellationToken cancellationToken = default)
    {
        var blobClient = _container.GetBlobClient(blobName);
        await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    // ── Tier conversion helpers ───────────────────────────────────────────────

    private static AccessTier ToAzureTier(BlobTier tier) => tier switch
    {
        BlobTier.Hot     => AccessTier.Hot,
        BlobTier.Cool    => AccessTier.Cool,
        BlobTier.Cold    => AccessTier.Cold,
        BlobTier.Archive => AccessTier.Archive,
        _                => throw new ArgumentOutOfRangeException(nameof(tier), tier, null)
    };

    private static BlobTier? FromAzureTier(AccessTier? tier)
    {
        if (tier is null)               return null;
        if (tier == AccessTier.Hot)     return BlobTier.Hot;
        if (tier == AccessTier.Cool)    return BlobTier.Cool;
        if (tier == AccessTier.Cold)    return BlobTier.Cold;
        if (tier == AccessTier.Archive) return BlobTier.Archive;
        return null;
    }

    private static AzureRehydratePriority ToAzureRehydratePriority(CoreRehydratePriority p) => p switch
    {
        CoreRehydratePriority.Standard => AzureRehydratePriority.Standard,
        CoreRehydratePriority.High     => AzureRehydratePriority.High,
        _                              => throw new ArgumentOutOfRangeException(nameof(p), p, null)
    };
}
