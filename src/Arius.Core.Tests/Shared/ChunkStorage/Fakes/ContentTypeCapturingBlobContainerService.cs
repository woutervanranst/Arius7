using Arius.Core.Shared.Storage;
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

    public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) =>
        _inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);

    public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default)
    {
        LastOpenWriteContentType = contentType;
        _openWriteContentTypes.Add(contentType);
        return _inner.OpenWriteAsync(blobName, contentType, cancellationToken);
    }

    public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) =>
        _inner.DownloadAsync(blobName, cancellationToken);

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default) =>
        _inner.GetMetadataAsync(blobName, cancellationToken);

    public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
        _inner.ListAsync(prefix, cancellationToken);

    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        _inner.SetMetadataAsync(blobName, metadata, cancellationToken);

    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) =>
        _inner.SetTierAsync(blobName, tier, cancellationToken);

    public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) =>
        _inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

    public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(blobName, cancellationToken);
}
