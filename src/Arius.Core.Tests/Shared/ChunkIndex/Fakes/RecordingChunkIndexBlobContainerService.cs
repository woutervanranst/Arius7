using System.Collections.Concurrent;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkIndex.Fakes;

internal sealed class RecordingChunkIndexBlobContainerService : IBlobContainerService
{
    private readonly ConcurrentDictionary<string, StoredBlob> _blobs = new(StringComparer.Ordinal);
    private readonly TimeSpan _chunkIndexUploadDelay;
    private string? _failUploadForPrefix;

    private int _activeChunkIndexUploads;
    private int _maxConcurrentChunkIndexUploads;
    private int _chunkIndexMetadataReads;
    private int _chunkIndexDownloads;

    public RecordingChunkIndexBlobContainerService(TimeSpan? chunkIndexUploadDelay = null, string? failUploadForPrefix = null)
    {
        _chunkIndexUploadDelay = chunkIndexUploadDelay ?? TimeSpan.Zero;
        _failUploadForPrefix = failUploadForPrefix;
    }

    public int MaxConcurrentChunkIndexUploads => Volatile.Read(ref _maxConcurrentChunkIndexUploads);
    public int ChunkIndexMetadataReads => Volatile.Read(ref _chunkIndexMetadataReads);
    public int ChunkIndexDownloads => Volatile.Read(ref _chunkIndexDownloads);

    public void ClearFailure() => _failUploadForPrefix = null;

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var isChunkIndex = blobName.StartsWith(BlobPaths.ChunkIndex, StringComparison.Ordinal);
        if (isChunkIndex)
        {
            var active = Interlocked.Increment(ref _activeChunkIndexUploads);
            UpdateMaxConcurrency(active);
            try
            {
                if (_chunkIndexUploadDelay > TimeSpan.Zero)
                    await Task.Delay(_chunkIndexUploadDelay, cancellationToken);

                if (_failUploadForPrefix is not null && blobName == BlobPaths.ChunkIndexShard(_failUploadForPrefix))
                    throw new IOException($"Simulated chunk-index upload failure for prefix {_failUploadForPrefix}.");

                await using var ms = new MemoryStream();
                await content.CopyToAsync(ms, cancellationToken);
                _blobs[blobName] = new StoredBlob(ms.ToArray(), new Dictionary<string, string>(metadata), tier, contentType, false);
            }
            finally
            {
                var remaining = Interlocked.Decrement(ref _activeChunkIndexUploads);
                UpdateMaxConcurrency(remaining);
            }
        }
        else
        {
            await using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            _blobs[blobName] = new StoredBlob(ms.ToArray(), new Dictionary<string, string>(metadata), tier, contentType, false);
        }
    }

    public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default)
        => Task.FromResult<Stream>(new CommitOnDisposeStream(bytes =>
        {
            _blobs[blobName] = new StoredBlob(bytes, new Dictionary<string, string>(), null, contentType, false);
        }));

    public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default)
    {
        if (blobName.StartsWith(BlobPaths.ChunkIndex, StringComparison.Ordinal))
            Interlocked.Increment(ref _chunkIndexDownloads);

        return Task.FromResult<Stream>(new MemoryStream(_blobs[blobName].Content, writable: false));
    }

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default)
    {
        if (blobName.StartsWith(BlobPaths.ChunkIndex, StringComparison.Ordinal))
            Interlocked.Increment(ref _chunkIndexMetadataReads);

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
            var current = Volatile.Read(ref _maxConcurrentChunkIndexUploads);
            if (active <= current)
                return;

            if (Interlocked.CompareExchange(ref _maxConcurrentChunkIndexUploads, active, current) == current)
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
