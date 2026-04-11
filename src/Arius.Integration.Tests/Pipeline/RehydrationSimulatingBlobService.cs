using Arius.Core.Shared.Storage;

namespace Arius.Integration.Tests.Pipeline;

/// <summary>
/// Wraps a real <see cref="IBlobContainerService"/> and overrides <see cref="GetMetadataAsync"/>
/// to simulate Archive-tier behaviour for a specific set of blob names, and <see cref="CopyAsync"/>
/// to record rehydration requests without actually copying.
/// Used to test restore pipeline rehydration state machine logic without real Azure Archive tier.
/// </summary>
internal sealed class RehydrationSimulatingBlobService(IBlobContainerService inner) : IBlobContainerService
{
    public HashSet<string> ArchiveTierBlobs { get; } = new(StringComparer.Ordinal);
    public HashSet<string> RehydratingBlobs { get; } = new(StringComparer.Ordinal);
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

    public async Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken ct = default)
    {
        var actual = await inner.GetMetadataAsync(blobName, ct);

        if (!actual.Exists)
            return actual;

        if (RehydratingBlobs.Contains(blobName))
            return new BlobMetadata
            {
                Exists = true,
                Tier = BlobTier.Archive,
                ContentLength = actual.ContentLength,
                IsRehydrating = true,
                Metadata = actual.Metadata,
            };

        if (ArchiveTierBlobs.Contains(blobName))
            return new BlobMetadata
            {
                Exists = true,
                Tier = BlobTier.Archive,
                ContentLength = actual.ContentLength,
                IsRehydrating = false,
                Metadata = actual.Metadata,
            };

        return actual;
    }

    public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct = default)
        => inner.ListAsync(prefix, ct);

    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct = default)
        => inner.SetMetadataAsync(blobName, metadata, ct);

    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken ct = default)
        => inner.SetTierAsync(blobName, tier, ct);

    public Task CopyAsync(string sourceBlobName, string destinationBlobName,
        BlobTier destinationTier, RehydratePriority? rehydratePriority = null,
        CancellationToken ct = default)
    {
        CopyCalls.Add((sourceBlobName, destinationBlobName));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string blobName, CancellationToken ct = default)
        => inner.DeleteAsync(blobName, ct);
}
