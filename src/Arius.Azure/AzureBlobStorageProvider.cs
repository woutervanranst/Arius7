using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Arius.Core.Infrastructure;
using Arius.Core.Models;

namespace Arius.Azure;

/// <summary>
/// Azure SDK–backed implementation of <see cref="IBlobStorageProvider"/>.
/// Each instance targets a single container specified at construction.
/// </summary>
public sealed class AzureBlobStorageProvider : IBlobStorageProvider
{
    private readonly BlobContainerClient _container;

    public AzureBlobStorageProvider(BlobContainerClient container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
    }

    // ── Container ────────────────────────────────────────────────────────────

    /// <summary>Creates the container if it does not already exist (idempotent).</summary>
    public async Task CreateContainerIfNotExistsAsync(CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
    }

    // ── IBlobStorageProvider ─────────────────────────────────────────────────

    public async Task UploadAsync(string blobName, Stream content, BlobTier tier, CancellationToken ct = default)
    {
        var client = _container.GetBlobClient(blobName);
        await client.UploadAsync(content, overwrite: true, cancellationToken: ct);

        // Set the access tier after upload.
        var azureTier = ToAccessTier(tier);
        await client.SetAccessTierAsync(azureTier, cancellationToken: ct);
    }

    public async Task<Stream> DownloadAsync(string blobName, CancellationToken ct = default)
    {
        var client = _container.GetBlobClient(blobName);
        var response = await client.DownloadStreamingAsync(cancellationToken: ct);
        return response.Value.Content;
    }

    public async IAsyncEnumerable<string> ListAsync(
        string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, ct))
        {
            yield return item.Name;
        }
    }

    public async Task DeleteAsync(string blobName, CancellationToken ct = default)
    {
        var client = _container.GetBlobClient(blobName);
        await client.DeleteIfExistsAsync(cancellationToken: ct);
    }

    public async Task SetTierAsync(string blobName, BlobTier tier, CancellationToken ct = default)
    {
        var client = _container.GetBlobClient(blobName);
        await client.SetAccessTierAsync(ToAccessTier(tier), cancellationToken: ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AccessTier ToAccessTier(BlobTier tier) => tier switch
    {
        BlobTier.Hot     => AccessTier.Hot,
        BlobTier.Cool    => AccessTier.Cool,
        BlobTier.Cold    => AccessTier.Cold,
        BlobTier.Archive => AccessTier.Archive,
        _                => throw new ArgumentOutOfRangeException(nameof(tier), tier, null)
    };
}
