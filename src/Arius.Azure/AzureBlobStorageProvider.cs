using Arius.Core.Application.Abstractions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace Arius.Azure;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IBlobStorageProvider"/>.
/// All operations target a single container identified at construction time.
/// </summary>
public sealed class AzureBlobStorageProvider : IBlobStorageProvider
{
    private readonly BlobContainerClient _container;

    /// <summary>Creates a provider using an explicit connection string and container name.</summary>
    public AzureBlobStorageProvider(string connectionString, string containerName)
        : this(new BlobContainerClient(connectionString, containerName))
    { }

    /// <summary>Creates a provider from a pre-configured <see cref="BlobContainerClient"/>.</summary>
    public AzureBlobStorageProvider(BlobContainerClient containerClient)
    {
        _container = containerClient;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private BlobClient GetBlob(string blobName) => _container.GetBlobClient(blobName);

    private static AccessTier ToAzureTier(BlobAccessTier tier) => tier switch
    {
        BlobAccessTier.Hot     => AccessTier.Hot,
        BlobAccessTier.Cool    => AccessTier.Cool,
        BlobAccessTier.Cold    => AccessTier.Cold,
        BlobAccessTier.Archive => AccessTier.Archive,
        _                      => throw new ArgumentOutOfRangeException(nameof(tier), tier, null)
    };

    private static BlobAccessTier? FromAzureTier(AccessTier? tier)
    {
        if (tier is null)
            return null;
        if (tier == AccessTier.Hot)     return BlobAccessTier.Hot;
        if (tier == AccessTier.Cool)    return BlobAccessTier.Cool;
        if (tier == AccessTier.Cold)    return BlobAccessTier.Cold;
        if (tier == AccessTier.Archive) return BlobAccessTier.Archive;
        return null; // Unknown tier value — treat as unknown
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask UploadAsync(
        string blobName,
        Stream data,
        BlobAccessTier tier,
        CancellationToken cancellationToken = default)
    {
        var blob = GetBlob(blobName);
        var options = new BlobUploadOptions
        {
            AccessTier = ToAzureTier(tier)
        };
        await blob.UploadAsync(data, options, cancellationToken).ConfigureAwait(false);
    }

    // ── Download ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<Stream> DownloadAsync(
        string blobName,
        CancellationToken cancellationToken = default)
    {
        var blob = GetBlob(blobName);
        var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.Value.Content;
    }

    // ── Metadata ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<bool> ExistsAsync(
        string blobName,
        CancellationToken cancellationToken = default)
    {
        var blob = GetBlob(blobName);
        var response = await blob.ExistsAsync(cancellationToken).ConfigureAwait(false);
        return response.Value;
    }

    /// <inheritdoc/>
    public async ValueTask<BlobAccessTier?> GetTierAsync(
        string blobName,
        CancellationToken cancellationToken = default)
    {
        var blob = GetBlob(blobName);
        var props = await blob.GetPropertiesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return FromAzureTier(props.Value.AccessTier);
    }

    /// <inheritdoc/>
    public async ValueTask<string?> GetArchiveStatusAsync(
        string blobName,
        CancellationToken cancellationToken = default)
    {
        var blob = GetBlob(blobName);
        var props = await blob.GetPropertiesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var status = props.Value.ArchiveStatus;
        return string.IsNullOrEmpty(status) ? null : status;
    }

    // ── Tiering ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask SetTierAsync(
        string blobName,
        BlobAccessTier tier,
        CancellationToken cancellationToken = default)
    {
        var blob = GetBlob(blobName);
        await blob.SetAccessTierAsync(ToAzureTier(tier), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async IAsyncEnumerable<Core.Application.Abstractions.BlobItem> ListAsync(
        string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (var page in _container
            .GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken)
            .AsPages()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            foreach (var item in page.Values)
            {
                yield return new Core.Application.Abstractions.BlobItem(
                    item.Name,
                    item.Properties.ContentLength,
                    item.Properties.LastModified);
            }
        }
    }

    // ── Deletion ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(
        string blobName,
        CancellationToken cancellationToken = default)
    {
        var blob = GetBlob(blobName);
        await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    // ── Lease (concurrency control) ───────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask<string> AcquireLeaseAsync(
        string blobName,
        CancellationToken cancellationToken = default)
    {
        var leaseClient = GetBlob(blobName).GetBlobLeaseClient();
        var response = await leaseClient.AcquireAsync(
            duration: TimeSpan.FromSeconds(60),
            cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return response.Value.LeaseId;
    }

    /// <inheritdoc/>
    public async ValueTask RenewLeaseAsync(
        string blobName,
        string leaseId,
        CancellationToken cancellationToken = default)
    {
        var leaseClient = GetBlob(blobName).GetBlobLeaseClient(leaseId);
        await leaseClient.RenewAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask ReleaseLeaseAsync(
        string blobName,
        string leaseId,
        CancellationToken cancellationToken = default)
    {
        var leaseClient = GetBlob(blobName).GetBlobLeaseClient(leaseId);
        await leaseClient.ReleaseAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
