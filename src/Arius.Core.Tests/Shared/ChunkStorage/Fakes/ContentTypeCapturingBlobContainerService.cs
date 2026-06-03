using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkStorage.Fakes;

/// <summary>
/// Records the <c>contentType</c> passed to <c>OpenWriteAsync</c> so upload tests can assert that
/// tar and large chunk writes choose the expected blob content types.
/// </summary>
internal sealed class ContentTypeCapturingBlobContainerService : IBlobContainerService
{
    private readonly FakeInMemoryBlobContainerService _inner = new();
    private readonly List<string?> _openWriteContentTypes = [];

    public string? LastOpenWriteContentType { get; private set; }
    public IReadOnlyList<string?> OpenWriteContentTypes => _openWriteContentTypes;

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) =>
        _inner.CreateContainerIfNotExistsAsync(cancellationToken);

    public Task<BlobMetadata> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) =>
        _inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default)
    {
        LastOpenWriteContentType = contentType;
        _openWriteContentTypes.Add(contentType);
        return _inner.OpenWriteAsync(blobName, contentType, cancellationToken);
    }

    public Task<Stream> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        _inner.DownloadAsync(blobName, cancellationToken);

    public Task<Stream?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        _inner.TryDownloadAsync(blobName, cancellationToken);

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        _inner.GetMetadataAsync(blobName, cancellationToken);

    public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata = false, CancellationToken cancellationToken = default) =>
        _inner.ListAsync(prefix, includeMetadata, cancellationToken);

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        _inner.SetMetadataAsync(blobName, metadata, cancellationToken);

    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) =>
        _inner.SetTierAsync(blobName, tier, cancellationToken);

    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) =>
        _inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(blobName, cancellationToken);
}
