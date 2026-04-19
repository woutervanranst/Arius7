using Arius.AzureBlob;
using Arius.Core.Shared.Storage;

namespace Arius.E2E.Tests.Services;

/// <summary>
/// Wraps <see cref="AzureBlobContainerService"/> and records all <see cref="CopyAsync"/> calls.
/// Used to verify the restore pipeline does not issue duplicate rehydration requests.
/// </summary>
internal sealed class CopyTrackingBlobService(AzureBlobContainerService inner) : IBlobContainerService
{
    public List<(string Source, string Destination)> CopyCalls { get; } = new();

    public Task CreateContainerIfNotExistsAsync(CancellationToken ct = default)
        => inner.CreateContainerIfNotExistsAsync(ct);

    public Task UploadAsync(string blobName, Stream content,
        IReadOnlyDictionary<string, string> metadata, BlobTier tier,
        string? contentType = null, bool overwrite = false, CancellationToken ct = default)
        => inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, ct);

    public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null,
        CancellationToken ct = default)
        => inner.OpenWriteAsync(blobName, contentType, ct);

    public Task<Stream> DownloadAsync(string blobName, CancellationToken ct = default)
        => inner.DownloadAsync(blobName, ct);

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken ct = default)
        => inner.GetMetadataAsync(blobName, ct);

    public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct = default)
        => inner.ListAsync(prefix, ct);

    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct = default)
        => inner.SetMetadataAsync(blobName, metadata, ct);

    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken ct = default)
        => inner.SetTierAsync(blobName, tier, ct);

    public async Task CopyAsync(string sourceBlobName, string destinationBlobName,
        BlobTier destinationTier, RehydratePriority? rehydratePriority = null,
        CancellationToken ct = default)
    {
        CopyCalls.Add((sourceBlobName, destinationBlobName));
        await inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, ct);
    }

    public Task DeleteAsync(string blobName, CancellationToken ct = default)
        => inner.DeleteAsync(blobName, ct);
}
