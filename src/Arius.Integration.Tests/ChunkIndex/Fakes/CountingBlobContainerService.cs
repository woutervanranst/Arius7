using Arius.Core.Shared.Storage;

namespace Arius.Integration.Tests.ChunkIndex.Fakes;

/// <summary>
/// Wraps an <see cref="IBlobContainerService"/> and counts chunk-index shard reads/writes, so Azurite-backed
/// scenario tests can assert how many TryDownloads / Uploads a chunk-index operation made against the real backend.
/// Only calls targeting <c>chunk-index/*</c> are counted; everything else is delegated untouched.
/// </summary>
internal sealed class CountingBlobContainerService(IBlobContainerService inner) : IBlobContainerService
{
    private int _chunkIndexTryDownloads;
    private int _chunkIndexUploads;
    private int _chunkIndexLists;

    public int ChunkIndexTryDownloads => Volatile.Read(ref _chunkIndexTryDownloads);
    public int ChunkIndexUploads      => Volatile.Read(ref _chunkIndexUploads);
    public int ChunkIndexLists        => Volatile.Read(ref _chunkIndexLists);

    public void Reset()
    {
        Interlocked.Exchange(ref _chunkIndexTryDownloads, 0);
        Interlocked.Exchange(ref _chunkIndexUploads, 0);
        Interlocked.Exchange(ref _chunkIndexLists, 0);
    }

    private static bool IsChunkIndex(RelativePath blobName) => blobName.StartsWith(BlobPaths.ChunkIndexPrefix);

    public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
    {
        if (IsChunkIndex(blobName))
            Interlocked.Increment(ref _chunkIndexTryDownloads);
        return inner.TryDownloadAsync(blobName, cancellationToken);
    }

    public Task<UploadResult> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        if (IsChunkIndex(blobName))
            Interlocked.Increment(ref _chunkIndexUploads);
        return inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);
    }

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default)
        => inner.CreateContainerIfNotExistsAsync(cancellationToken);

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default)
        => inner.OpenWriteAsync(blobName, contentType, cancellationToken);

    public Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => inner.DownloadAsync(blobName, cancellationToken);

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => inner.GetMetadataAsync(blobName, cancellationToken);

    public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata, CancellationToken cancellationToken = default)
        => inner.ListAsync(prefix, includeMetadata, cancellationToken);

    public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, BlobListPrefixKind prefixKind, bool includeMetadata = false, CancellationToken cancellationToken = default)
    {
        if (IsChunkIndex(prefix))
            Interlocked.Increment(ref _chunkIndexLists);
        return inner.ListAsync(prefix, prefixKind, includeMetadata, cancellationToken);
    }

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        => inner.SetMetadataAsync(blobName, metadata, cancellationToken);

    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default)
        => inner.SetTierAsync(blobName, tier, cancellationToken);

    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default)
        => inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => inner.DeleteAsync(blobName, cancellationToken);
}
