using System.Collections.Concurrent;
using System.IO.Compression;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;

namespace Arius.Tests.Shared.Storage;

/// <summary>
/// Shared in-memory <see cref="IBlobContainerService"/> test double.
/// It is intentionally stateful and configurable so tests can model completed blobs,
/// metadata-only reads, optimistic concurrency conflicts, and rerun recovery flows
/// without depending on Azurite or real Azure-specific error behavior.
/// </summary>
public sealed class FakeInMemoryBlobContainerService : IBlobContainerService
{
    private readonly ConcurrentDictionary<RelativePath, StoredBlob> _blobs = new();
    private readonly ConcurrentDictionary<RelativePath, int> _openWriteAlreadyExists = new();
    private long _etagSequence;

    private readonly ConcurrentQueue<RelativePath> _requestedBlobNames = new();
    private readonly ConcurrentQueue<RelativePath> _deletedBlobNames = new();
    private readonly ConcurrentQueue<RelativePath> _uploadedBlobNames = new();

    public ICollection<RelativePath> RequestedBlobNames => new RecordingCollection(_requestedBlobNames);
    public ICollection<RelativePath> DeletedBlobNames => new RecordingCollection(_deletedBlobNames);
    public ICollection<RelativePath> UploadedBlobNames => new RecordingCollection(_uploadedBlobNames);

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<UploadResult> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        await using var ms = new MemoryStream();
        await content.CopyToAsync(ms, cancellationToken);

        if (!overwrite && _blobs.ContainsKey(blobName))
            throw new BlobAlreadyExistsException(blobName);

        var etag = NextETag(blobName);
        _blobs[blobName] = new StoredBlob(ms.ToArray(), new Dictionary<string, string>(metadata), tier, contentType, false, etag);
        _uploadedBlobNames.Enqueue(blobName);
        return new UploadResult
        {
            ETag = etag,
        };
    }

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default)
    {
        if (_openWriteAlreadyExists.TryGetValue(blobName, out var remaining) && remaining > 0)
        {
            if (remaining == 1)
                _openWriteAlreadyExists.TryRemove(blobName, out _);
            else
                _openWriteAlreadyExists[blobName] = remaining - 1;

            throw new BlobAlreadyExistsException(blobName);
        }

        if (_blobs.ContainsKey(blobName))
            throw new BlobAlreadyExistsException(blobName);

        return Task.FromResult<Stream>(new CommitOnDisposeStream(bytes =>
        {
            _blobs[blobName] = new StoredBlob(bytes, new Dictionary<string, string>(), null, contentType, false, NextETag(blobName));
            _uploadedBlobNames.Enqueue(blobName);
        }));
    }

    public async Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => await TryDownloadAsync(blobName, cancellationToken) ?? throw new BlobNotFoundException(blobName);

    public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
    {
        _requestedBlobNames.Enqueue(blobName);
        return Task.FromResult(_blobs.TryGetValue(blobName, out var blob)
            ? new DownloadResult
            {
                Stream = new MemoryStream(blob.Content, writable: false),
                ETag = blob.ETag,
            }
            : null);
    }

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default)
    {
        _requestedBlobNames.Enqueue(blobName);
        if (!_blobs.TryGetValue(blobName, out var blob))
            return Task.FromResult(new BlobMetadata { Exists = false });

        return Task.FromResult(new BlobMetadata
        {
            Exists = true,
            ETag = blob.ETag,
            Tier = blob.Tier,
            ContentLength = blob.Content.LongLength,
            IsRehydrating = blob.IsRehydrating,
            Metadata = new Dictionary<string, string>(blob.Metadata)
        });
    }

    public async IAsyncEnumerable<BlobListItem> ListAsync(
        RelativePath prefix,
        bool includeMetadata = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _requestedBlobNames.Enqueue(prefix);

        foreach (var blobName in _blobs.Keys
                     .Where(name => name.StartsWith(prefix))
                     .OrderBy(name => name.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyDictionary<string, string>? metadata = null;
            long? contentLength = null;

            if (includeMetadata)
            {
                if (!_blobs.TryGetValue(blobName, out var blob))
                    continue;

                metadata = new Dictionary<string, string>(blob.Metadata);
                contentLength = blob.Content.LongLength;
            }

            yield return new BlobListItem
            {
                Name = blobName,
                ETag = _blobs.TryGetValue(blobName, out var currentBlob) ? currentBlob.ETag : null,
                Metadata = metadata,
                ContentLength = contentLength,
                Tier = currentBlob?.Tier,
            };
            await Task.Yield();
        }
    }

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        var blob = _blobs[blobName];
        _blobs[blobName] = blob with { Metadata = new Dictionary<string, string>(metadata) };
        return Task.CompletedTask;
    }

    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default)
    {
        var blob = _blobs[blobName];
        _blobs[blobName] = blob with { Tier = tier };
        return Task.CompletedTask;
    }

    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default)
    {
        var source = _blobs[sourceBlobName];
        _blobs[destinationBlobName] = source with { Tier = destinationTier, IsRehydrating = false, ETag = NextETag(destinationBlobName) };
        return Task.CompletedTask;
    }

    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default)
    {
        _deletedBlobNames.Enqueue(blobName);
        _blobs.TryRemove(blobName, out _);
        _openWriteAlreadyExists.TryRemove(blobName, out _);
        return Task.CompletedTask;
    }

    public void ThrowAlreadyExistsOnOpenWrite(RelativePath blobName, bool throwOnce = false)
        => _openWriteAlreadyExists[blobName] = throwOnce ? 1 : int.MaxValue;

    public void SeedBlob(RelativePath blobName, byte[] content, BlobTier? tier = null, IReadOnlyDictionary<string, string>? metadata = null, string? contentType = null, bool isRehydrating = false)
        => _blobs[blobName] = new StoredBlob(content, metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata), tier, contentType, isRehydrating, NextETag(blobName));

    public void SetETag(RelativePath blobName, string etag)
    {
        var blob = _blobs[blobName];
        _blobs[blobName] = blob with { ETag = etag };
    }


    public void ClearMetadata(RelativePath blobPath)
    {
        var blob = _blobs[blobPath];
        _blobs[blobPath] = blob with { Metadata = new Dictionary<string, string>() };
    }

    public async Task SeedLargeBlobAsync(RelativePath blobName, byte[] originalContent, BlobTier tier)
    {
        var payload = await GzipAsync(originalContent);
        SeedBlob(
            blobName,
            payload,
            tier,
            new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType]    = BlobMetadataKeys.TypeLarge,
                [BlobMetadataKeys.OriginalSize] = originalContent.Length.ToString(),
                [BlobMetadataKeys.ChunkSize]    = payload.Length.ToString(),
            },
            ContentTypes.LargePlaintext);
    }

    public async Task SeedTarBlobAsync(RelativePath blobName, IReadOnlyList<byte[]> originalContents, BlobTier tier)
    {
        var combined = originalContents.SelectMany(bytes => bytes).ToArray();
        var payload = await GzipAsync(combined);
        SeedBlob(
            blobName,
            payload,
            tier,
            new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeTar,
                [BlobMetadataKeys.ChunkSize] = payload.Length.ToString(),
            },
            ContentTypes.TarPlaintext);
    }

    private static async Task<byte[]> GzipAsync(byte[] content)
    {
        await using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            await gzip.WriteAsync(content);
        }

        return compressed.ToArray();
    }

    private sealed record StoredBlob(
        byte[] Content,
        Dictionary<string, string> Metadata,
        BlobTier? Tier,
        string? ContentType,
        bool IsRehydrating,
        string ETag);

    private string NextETag(RelativePath blobName)
        => $"fake:{blobName}:{Interlocked.Increment(ref _etagSequence)}";

    private sealed class CommitOnDisposeStream(Action<byte[]> onCommit) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                onCommit(ToArray());

            base.Dispose(disposing);
        }
    }

    private sealed class RecordingCollection(ConcurrentQueue<RelativePath> queue) : ICollection<RelativePath>
    {
        public int Count => queue.Count;
        public bool IsReadOnly => false;

        public void Add(RelativePath item) => queue.Enqueue(item);

        public void Clear()
        {
            while (queue.TryDequeue(out _))
            {
            }
        }

        public bool Contains(RelativePath item) => queue.Contains(item);

        public void CopyTo(RelativePath[] array, int arrayIndex) => queue.ToArray().CopyTo(array, arrayIndex);

        public bool Remove(RelativePath item)
        {
            var removed = false;
            var retained = new List<RelativePath>();

            while (queue.TryDequeue(out var current))
            {
                if (!removed && current == item)
                {
                    removed = true;
                    continue;
                }

                retained.Add(current);
            }

            foreach (var current in retained)
                queue.Enqueue(current);

            return removed;
        }

        public IEnumerator<RelativePath> GetEnumerator() => ((IEnumerable<RelativePath>)queue.ToArray()).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
