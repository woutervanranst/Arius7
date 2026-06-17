namespace Arius.Core.Tests.Shared.FileTree.Fakes;

/// <summary>
/// Blob-container fake that fails every download with one supplied exception.
/// It exists to exercise <see cref="FileTreeService"/> read paths that must propagate
/// a download failure instead of hanging or masking the error behind cache coordination.
/// </summary>
internal sealed class ThrowingDownloadBlobContainerService(Exception exception) : IBlobContainerService
{
    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<UploadResult> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromException<DownloadResult>(exception);

    public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromException<DownloadResult?>(exception);

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BlobMetadata { Exists = false });

    public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, BlobListPrefixKind prefixKind = BlobListPrefixKind.DirectoryPrefix, bool includeMetadata = false, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<BlobListItem>();

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
