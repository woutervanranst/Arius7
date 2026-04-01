using Arius.Core.Storage;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using AzureRehydratePriority = Azure.Storage.Blobs.Models.RehydratePriority;
using CoreRehydratePriority = Arius.Core.Storage.RehydratePriority;

namespace Arius.AzureBlob;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IBlobContainerService"/>.
/// Operates against a single Azure Blob container.
/// </summary>
public sealed class AzureBlobContainerService : IBlobContainerService
{
    private readonly BlobContainerClient _container;

    public AzureBlobContainerService(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;
    }

    // ── Container ─────────────────────────────────────────────────────────────

    public async Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    public async Task UploadAsync(
        string                              blobName,
        Stream                              content,
        IReadOnlyDictionary<string, string> metadata,
        BlobTier                            tier,
        string?                             contentType       = null,
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

        if (contentType is not null)
        {
            uploadOptions.HttpHeaders = new BlobHttpHeaders
            {
                ContentType = contentType
            };
        }

        try
        {
            await blobClient.UploadAsync(content, uploadOptions, cancellationToken);
        }
        catch (RequestFailedException ex) when (IsAlreadyExistsError(ex))
        {
            throw new BlobAlreadyExistsException(blobName);
        }
    }

    public async Task<Stream> OpenWriteAsync(
        string            blobName,
        string?           contentType       = null,
        CancellationToken cancellationToken = default)
    {
        var blobClient = _container.GetBlockBlobClient(blobName);

        var openWriteOptions = new BlockBlobOpenWriteOptions
        {
            HttpHeaders      = contentType is not null
                ? new BlobHttpHeaders { ContentType = contentType }
                : null,
            // Optimistic concurrency: fail if blob already exists.
            // BlockBlobClient.OpenWriteAsync only supports overwrite:true; passing false throws
            // ArgumentException immediately. Use IfNoneMatch=* on the open-write condition instead —
            // Azure enforces this on the initial PutBlob and throws 412 ConditionNotMet if the blob exists.
            OpenConditions   = new BlobRequestConditions { IfNoneMatch = ETag.All },
        };

        try
        {
            return await blobClient.OpenWriteAsync(overwrite: true, openWriteOptions, cancellationToken);
        }
        catch (RequestFailedException ex) when (IsAlreadyExistsError(ex))
        {
            throw new BlobAlreadyExistsException(blobName);
        }
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
                IsRehydrating = p.ArchiveStatus is "rehydrate-pending-to-hot" or "rehydrate-pending-to-cool" or "rehydrate-pending-to-cold",
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

    public async Task SetTierAsync(
        string            blobName,
        BlobTier          tier,
        CancellationToken cancellationToken = default)
    {
        var blobClient = _container.GetBlobClient(blobName);
        await blobClient.SetAccessTierAsync(ToAzureTier(tier), cancellationToken: cancellationToken);
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

    /// <summary>
    /// Returns true for any RequestFailedException that means "the blob already exists".
    /// <para>
    /// Azure Storage returns 412 ConditionNotMet when <c>IfNoneMatch=*</c> is set and the blob exists.
    /// Azurite (the local emulator) returns 409 BlobAlreadyExists for the same condition on
    /// <see cref="BlockBlobClient.OpenWriteAsync"/>. Both are treated identically.
    /// </para>
    /// </summary>
    private static bool IsAlreadyExistsError(RequestFailedException ex) =>
        (ex.Status == 412 && ex.ErrorCode == "ConditionNotMet") ||
        (ex.Status == 409 && ex.ErrorCode == "BlobAlreadyExists");
}
