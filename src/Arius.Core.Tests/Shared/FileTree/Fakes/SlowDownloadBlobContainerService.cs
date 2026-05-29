using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.FileTree.Fakes;

/// <summary>
/// Blob-container fake that blocks the first download until the test releases it, while delegating
/// all storage state to <see cref="FakeInMemoryBlobContainerService"/>.
/// It exists to create deterministic concurrent-read races so filetree cache publication behavior
/// can be verified without relying on timing-sensitive sleeps.
/// </summary>
internal sealed class SlowDownloadBlobContainerService : IBlobContainerService
{
    private readonly FakeInMemoryBlobContainerService _inner = new();
    private readonly TaskCompletionSource _firstDownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseFirstDownload = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _downloadCount;

    public Task FirstDownloadStarted => _firstDownloadStarted.Task;
    public ICollection<RelativePath> RequestedBlobNames => _inner.RequestedBlobNames;

    public void SeedBlob(RelativePath blobName, byte[] content, BlobTier? tier = null, IReadOnlyDictionary<string, string>? metadata = null, string? contentType = null, bool isRehydrating = false)
        => _inner.SeedBlob(blobName, content, tier, metadata, contentType, isRehydrating);

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default)
        => _inner.CreateContainerIfNotExistsAsync(cancellationToken);

    public Task UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
        => _inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default)
        => _inner.OpenWriteAsync(blobName, contentType, cancellationToken);

    public async Task<Stream> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
    {
        var count = Interlocked.Increment(ref _downloadCount);
        if (count == 1)
        {
            _firstDownloadStarted.TrySetResult();
            await _releaseFirstDownload.Task.WaitAsync(cancellationToken);
        }

        return await _inner.DownloadAsync(blobName, cancellationToken);
    }

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => _inner.GetMetadataAsync(blobName, cancellationToken);

    public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata = false, CancellationToken cancellationToken = default)
        => _inner.ListAsync(prefix, includeMetadata, cancellationToken);

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        => _inner.SetMetadataAsync(blobName, metadata, cancellationToken);

    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default)
        => _inner.SetTierAsync(blobName, tier, cancellationToken);

    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default)
        => _inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => _inner.DeleteAsync(blobName, cancellationToken);

    public void ReleaseFirstDownload() => _releaseFirstDownload.TrySetResult();
}
