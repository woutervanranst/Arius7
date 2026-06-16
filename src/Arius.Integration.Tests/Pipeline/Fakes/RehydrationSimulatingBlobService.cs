using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;

namespace Arius.Integration.Tests.Pipeline.Fakes;

/// <summary>
/// Wraps a real <see cref="IBlobContainerService"/> and overrides <see cref="GetMetadataAsync"/>
/// to simulate Archive-tier behaviour for a specific set of blob names, and <see cref="CopyAsync"/>
/// to record rehydration requests without actually copying.
/// Used to test restore pipeline rehydration state machine logic without real Azure Archive tier.
/// </summary>
internal sealed class RehydrationSimulatingBlobService(IBlobContainerService inner) : IBlobContainerService
{
    public HashSet<RelativePath> ArchiveTierBlobs { get; } = [];
    public HashSet<RelativePath> RehydratingBlobs { get; } = [];
    public List<(RelativePath Source, RelativePath Destination)> CopyCalls { get; } = [];

    public Task CreateContainerIfNotExistsAsync(CancellationToken ct = default)
        => inner.CreateContainerIfNotExistsAsync(ct);

    public Task<UploadResult> UploadAsync(RelativePath blobName, Stream content,
        IReadOnlyDictionary<string, string> metadata, BlobTier tier,
        string? contentType = null, bool overwrite = false, CancellationToken ct = default)
        => inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, ct);

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null,
        CancellationToken ct = default)
        => inner.OpenWriteAsync(blobName, contentType, ct);

    public Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken ct = default)
    {
        if (ArchiveTierBlobs.Contains(blobName) || RehydratingBlobs.Contains(blobName))
            throw new BlobArchivedException(blobName);

        return inner.DownloadAsync(blobName, ct);
    }

    public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken ct = default)
    {
        if (ArchiveTierBlobs.Contains(blobName) || RehydratingBlobs.Contains(blobName))
            throw new BlobArchivedException(blobName);

        return inner.TryDownloadAsync(blobName, ct);
    }

    public async Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken ct = default)
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

    public async IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in inner.ListAsync(prefix, includeMetadata, ct))
            yield return item;

        if (prefix != BlobPaths.ChunksRehydratedPrefix)
            yield break;

        foreach (var blobName in RehydratingBlobs.OrderBy(static path => path.ToString(), StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            yield return new BlobListItem
            {
                Name = BlobPaths.ChunkRehydratedPath(ChunkHash.Parse(blobName.Name.ToString())),
                Tier = BlobTier.Archive,
            };
        }
    }

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata,
        CancellationToken ct = default)
        => inner.SetMetadataAsync(blobName, metadata, ct);

    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken ct = default)
        => inner.SetTierAsync(blobName, tier, ct);

    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName,
        BlobTier destinationTier, RehydratePriority? rehydratePriority = null,
        CancellationToken ct = default)
    {
        CopyCalls.Add((sourceBlobName, destinationBlobName));
        RehydratingBlobs.Add(sourceBlobName);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(RelativePath blobName, CancellationToken ct = default)
        => inner.DeleteAsync(blobName, ct);
}
