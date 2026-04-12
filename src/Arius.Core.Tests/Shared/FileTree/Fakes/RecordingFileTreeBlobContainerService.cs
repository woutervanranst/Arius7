using System.Collections.Concurrent;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Shared.FileTree.Fakes;

internal sealed class RecordingFileTreeBlobContainerService : IBlobContainerService
{
    private readonly ConcurrentDictionary<string, StoredBlob> _blobs = new(StringComparer.Ordinal);
    private readonly TimeSpan _fileTreeUploadDelay;
    private readonly TaskCompletionSource _firstFileTreeUploadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allowFileTreeUploads = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _blockFileTreeUploads;
    private readonly bool _throwOnFileTreeUpload;

    private int _activeFileTreeUploads;
    private int _maxConcurrentFileTreeUploads;

    public RecordingFileTreeBlobContainerService(TimeSpan? fileTreeUploadDelay = null, bool blockFileTreeUploads = false, bool throwOnFileTreeUpload = false)
    {
        _fileTreeUploadDelay = fileTreeUploadDelay ?? TimeSpan.Zero;
        _blockFileTreeUploads = blockFileTreeUploads;
        _throwOnFileTreeUpload = throwOnFileTreeUpload;

        if (!blockFileTreeUploads)
            _allowFileTreeUploads.TrySetResult();
    }

    public int MaxConcurrentFileTreeUploads => Volatile.Read(ref _maxConcurrentFileTreeUploads);
    public Task FirstFileTreeUploadStarted => _firstFileTreeUploadStarted.Task;

    public void AllowFileTreeUploads() => _allowFileTreeUploads.TrySetResult();

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        if (!overwrite && _blobs.ContainsKey(blobName))
            throw new BlobAlreadyExistsException(blobName);

        var isFileTree = blobName.StartsWith(BlobPaths.FileTrees, StringComparison.Ordinal);
        if (isFileTree)
        {
            _firstFileTreeUploadStarted.TrySetResult();
            var active = Interlocked.Increment(ref _activeFileTreeUploads);
            UpdateMaxConcurrency(active);
            await _allowFileTreeUploads.Task.WaitAsync(cancellationToken);
            if (_throwOnFileTreeUpload)
                throw new IOException("Simulated filetree upload failure");
            if (_fileTreeUploadDelay > TimeSpan.Zero)
                await Task.Delay(_fileTreeUploadDelay, cancellationToken);
        }

        try
        {
            await using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            _blobs[blobName] = new StoredBlob(ms.ToArray(), new Dictionary<string, string>(metadata), tier, contentType, false);
        }
        finally
        {
            if (isFileTree)
                Interlocked.Decrement(ref _activeFileTreeUploads);
        }
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

    private void UpdateMaxConcurrency(int active)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maxConcurrentFileTreeUploads);
            if (active <= current)
                return;

            if (Interlocked.CompareExchange(ref _maxConcurrentFileTreeUploads, active, current) == current)
                return;
        }
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
