using Arius.Core.Shared.Storage;
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

    public ICollection<string> DeletedBlobNames => _inner.DeletedBlobNames;

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) =>
        _inner.CreateContainerIfNotExistsAsync(cancellationToken);

    public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) =>
        _inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);

    public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
        _inner.OpenWriteAsync(blobName, contentType, cancellationToken);

    public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) =>
        _inner.DownloadAsync(blobName, cancellationToken);

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default) =>
        _inner.GetMetadataAsync(blobName, cancellationToken);

    public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
        _inner.ListAsync(prefix, cancellationToken);

    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _remainingFailures, 0) == 1)
            throw new BlobAlreadyExistsException(blobName);

        return _inner.SetMetadataAsync(blobName, metadata, cancellationToken);
    }

    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) =>
        _inner.SetTierAsync(blobName, tier, cancellationToken);

    public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) =>
        _inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

    public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(blobName, cancellationToken);
}
