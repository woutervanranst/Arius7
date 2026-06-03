using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkStorage.Fakes;

/// <summary>
/// Simulates a thin-chunk metadata commit race by failing the first <c>SetMetadataAsync</c>
/// call with <see cref="BlobAlreadyExistsException"/> so upload tests can verify cleanup and retry behavior.
/// </summary>
internal sealed class BlobAlreadyExistsOnSetMetadataOnceBlobContainerService : IBlobContainerService
{
    private readonly FakeInMemoryBlobContainerService _inner = new();
    private int _remainingFailures = 1;

    public ICollection<RelativePath> DeletedBlobNames => _inner.DeletedBlobNames;

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) =>
        _inner.CreateContainerIfNotExistsAsync(cancellationToken);

    public Task<BlobMetadata> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) =>
        _inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
        _inner.OpenWriteAsync(blobName, contentType, cancellationToken);

    public Task<Stream> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        _inner.DownloadAsync(blobName, cancellationToken);

    public Task<Stream?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        _inner.TryDownloadAsync(blobName, cancellationToken);

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        _inner.GetMetadataAsync(blobName, cancellationToken);

    public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata = false, CancellationToken cancellationToken = default) =>
        _inner.ListAsync(prefix, includeMetadata, cancellationToken);

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _remainingFailures, 0) == 1)
            throw new BlobAlreadyExistsException(blobName);

        return _inner.SetMetadataAsync(blobName, metadata, cancellationToken);
    }

    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) =>
        _inner.SetTierAsync(blobName, tier, cancellationToken);

    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) =>
        _inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(blobName, cancellationToken);
}
