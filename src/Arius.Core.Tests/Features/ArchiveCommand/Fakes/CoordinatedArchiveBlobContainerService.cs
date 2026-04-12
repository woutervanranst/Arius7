using System.Collections.Concurrent;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Features.ArchiveCommand.Fakes;

internal sealed class CoordinatedArchiveBlobContainerService : IBlobContainerService
{
    private readonly ConcurrentDictionary<string, StoredBlob> _blobs = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _treeUploadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allowTreeUpload = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _treeUploadCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allowIndexUpload = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _indexUploadCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task TreeUploadStarted => _treeUploadStarted.Task;
    public Task TreeUploadCompleted => _treeUploadCompleted.Task;
    public Task IndexUploadCompleted => _indexUploadCompleted.Task;

    public bool SnapshotUploadedBeforeIndexCompleted { get; private set; }

    public void AllowTreeUpload() => _allowTreeUpload.TrySetResult();

    public void AllowIndexUpload() => _allowIndexUpload.TrySetResult();

    public bool HasAnyBlobWithPrefix(string prefix) => _blobs.Keys.Any(name => name.StartsWith(prefix, StringComparison.Ordinal));

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        if (blobName.StartsWith(BlobPaths.FileTrees, StringComparison.Ordinal))
        {
            _treeUploadStarted.TrySetResult();
            await _allowTreeUpload.Task.WaitAsync(cancellationToken);
        }

        if (blobName.StartsWith(BlobPaths.ChunkIndex, StringComparison.Ordinal))
            await _allowIndexUpload.Task.WaitAsync(cancellationToken);

        if (blobName.StartsWith(BlobPaths.Snapshots, StringComparison.Ordinal) && !_indexUploadCompleted.Task.IsCompleted)
            SnapshotUploadedBeforeIndexCompleted = true;

        await using var ms = new MemoryStream();
        await content.CopyToAsync(ms, cancellationToken);
        _blobs[blobName] = new StoredBlob(ms.ToArray(), new Dictionary<string, string>(metadata), tier, contentType, false);

        if (blobName.StartsWith(BlobPaths.FileTrees, StringComparison.Ordinal))
            _treeUploadCompleted.TrySetResult();

        if (blobName.StartsWith(BlobPaths.ChunkIndex, StringComparison.Ordinal))
            _indexUploadCompleted.TrySetResult();
    }

    public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default)
        => Task.FromResult<Stream>(new CommitOnDisposeStream(bytes =>
        {
            _blobs[blobName] = new StoredBlob(bytes, new Dictionary<string, string>(), null, contentType, false);
        }));

    public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default)
        => Task.FromResult<Stream>(new MemoryStream(_blobs[blobName].Content, writable: false));

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default)
    {
        if (!_blobs.TryGetValue(blobName, out var blob))
            return Task.FromResult(new BlobMetadata { Exists = false });

        return Task.FromResult(new BlobMetadata
        {
            Exists = true,
            Tier = blob.Tier,
            ContentLength = blob.Content.LongLength,
            IsRehydrating = blob.IsRehydrating,
            Metadata = new Dictionary<string, string>(blob.Metadata)
        });
    }

    public async IAsyncEnumerable<string> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var blobName in _blobs.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return blobName;
            await Task.Yield();
        }
    }

    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        var blob = _blobs[blobName];
        _blobs[blobName] = blob with { Metadata = new Dictionary<string, string>(metadata) };
        return Task.CompletedTask;
    }

    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default)
    {
        var blob = _blobs[blobName];
        _blobs[blobName] = blob with { Tier = tier };
        return Task.CompletedTask;
    }

    public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default)
    {
        var source = _blobs[sourceBlobName];
        _blobs[destinationBlobName] = source with { Tier = destinationTier, IsRehydrating = false };
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default)
    {
        _blobs.TryRemove(blobName, out _);
        return Task.CompletedTask;
    }

    private sealed record StoredBlob(
        byte[] Content,
        Dictionary<string, string> Metadata,
        BlobTier? Tier,
        string? ContentType,
        bool IsRehydrating);

    private sealed class CommitOnDisposeStream(Action<byte[]> onCommit) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                onCommit(ToArray());

            base.Dispose(disposing);
        }
    }
}
