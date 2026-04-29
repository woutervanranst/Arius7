using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.FileTree.Fakes;

internal sealed class SlowDownloadBlobContainerService : IBlobContainerService
{
    private readonly FakeInMemoryBlobContainerService _inner = new();
    private readonly TaskCompletionSource _firstDownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseFirstDownload = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _downloadCount;

    public Task FirstDownloadStarted => _firstDownloadStarted.Task;
    public ICollection<string> RequestedBlobNames => _inner.RequestedBlobNames;

    public void SeedBlob(string blobName, byte[] content, BlobTier? tier = null, IReadOnlyDictionary<string, string>? metadata = null, string? contentType = null, bool isRehydrating = false)
        => _inner.SeedBlob(blobName, content, tier, metadata, contentType, isRehydrating);

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default)
        => _inner.CreateContainerIfNotExistsAsync(cancellationToken);

    public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
        => _inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);

    public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default)
        => _inner.OpenWriteAsync(blobName, contentType, cancellationToken);

    public async Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default)
    {
        var count = Interlocked.Increment(ref _downloadCount);
        if (count == 1)
        {
            _firstDownloadStarted.TrySetResult();
            await _releaseFirstDownload.Task.WaitAsync(cancellationToken);
        }

        return await _inner.DownloadAsync(blobName, cancellationToken);
    }

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default)
        => _inner.GetMetadataAsync(blobName, cancellationToken);

    public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default)
        => _inner.ListAsync(prefix, cancellationToken);

    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        => _inner.SetMetadataAsync(blobName, metadata, cancellationToken);

    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default)
        => _inner.SetTierAsync(blobName, tier, cancellationToken);

    public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default)
        => _inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

    public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default)
        => _inner.DeleteAsync(blobName, cancellationToken);

    public void ReleaseFirstDownload() => _releaseFirstDownload.TrySetResult();
}
