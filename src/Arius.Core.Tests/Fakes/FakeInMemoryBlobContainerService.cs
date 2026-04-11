using Arius.Core.Shared.Storage;
using System.Collections.Concurrent;
using System.IO.Compression;

namespace Arius.Core.Tests.Fakes;

/// <summary>
/// Shared in-memory <see cref="IBlobContainerService"/> test double for core tests.
/// It is intentionally stateful and configurable so tests can model completed blobs,
/// metadata-only reads, optimistic concurrency conflicts, and rerun recovery flows
/// without depending on Azurite or real Azure-specific error behavior.
/// Use this fake when a test needs full blob lifecycle behavior: uploads, open-write conflicts,
/// metadata updates, deletes, copy/tier changes, or seeded blob contents.
/// </summary>
internal sealed class FakeInMemoryBlobContainerService : IBlobContainerService
{
    private readonly ConcurrentDictionary<string, StoredBlob> _blobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _openWriteAlreadyExists = new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<string> _requestedBlobNames = new();
    private readonly ConcurrentQueue<string> _deletedBlobNames = new();
    private readonly ConcurrentQueue<string> _uploadedBlobNames = new();

    public ICollection<string> RequestedBlobNames => new RecordingCollection(_requestedBlobNames);
    public ICollection<string> DeletedBlobNames => new RecordingCollection(_deletedBlobNames);
    public ICollection<string> UploadedBlobNames => new RecordingCollection(_uploadedBlobNames);

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        await using var ms = new MemoryStream();
        await content.CopyToAsync(ms, cancellationToken);

        if (!overwrite && _blobs.ContainsKey(blobName))
            throw new BlobAlreadyExistsException(blobName);

        _blobs[blobName] = new StoredBlob(ms.ToArray(), new Dictionary<string, string>(metadata), tier, contentType, false);
        _uploadedBlobNames.Enqueue(blobName);
    }

    public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default)
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
            _blobs[blobName] = new StoredBlob(bytes, new Dictionary<string, string>(), null, contentType, false);
            _uploadedBlobNames.Enqueue(blobName);
        }));
    }

    public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default)
    {
        _requestedBlobNames.Enqueue(blobName);
        var blob = _blobs[blobName];
        return Task.FromResult<Stream>(new MemoryStream(blob.Content, writable: false));
    }

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default)
    {
        _requestedBlobNames.Enqueue(blobName);
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
        _deletedBlobNames.Enqueue(blobName);
        _blobs.TryRemove(blobName, out _);
        _openWriteAlreadyExists.TryRemove(blobName, out _);
        return Task.CompletedTask;
    }

    public void ThrowAlreadyExistsOnOpenWrite(string blobName, bool throwOnce = false)
        => _openWriteAlreadyExists[blobName] = throwOnce ? 1 : int.MaxValue;

    public void SeedBlob(string blobName, byte[] content, BlobTier? tier = null, IReadOnlyDictionary<string, string>? metadata = null, string? contentType = null, bool isRehydrating = false)
        => _blobs[blobName] = new StoredBlob(content, metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(metadata), tier, contentType, isRehydrating);

    public void SeedMetadata(string blobName, BlobMetadata metadata)
        => _blobs[blobName] = new StoredBlob(Array.Empty<byte>(), new Dictionary<string, string>(metadata.Metadata), metadata.Tier, null, metadata.IsRehydrating);

    public void ClearMetadata(string blobName)
    {
        var blob = _blobs[blobName];
        _blobs[blobName] = blob with { Metadata = new Dictionary<string, string>() };
    }

    public async Task SeedLargeBlobAsync(string blobName, byte[] originalContent, BlobTier tier)
    {
        var payload = await GzipAsync(originalContent);
        SeedBlob(
            blobName,
            payload,
            tier,
            new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeLarge,
                [BlobMetadataKeys.OriginalSize] = originalContent.Length.ToString(),
                [BlobMetadataKeys.ChunkSize] = payload.Length.ToString(),
            },
            ContentTypes.LargePlaintext);
    }

    public async Task SeedTarBlobAsync(string blobName, IReadOnlyList<byte[]> originalContents, BlobTier tier)
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
        await using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
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

    private sealed class RecordingCollection(ConcurrentQueue<string> queue) : ICollection<string>
    {
        public int Count => queue.Count;
        public bool IsReadOnly => false;

        public void Add(string item) => queue.Enqueue(item);

        public void Clear()
        {
            while (queue.TryDequeue(out _))
            {
            }
        }

        public bool Contains(string item) => queue.Contains(item);

        public void CopyTo(string[] array, int arrayIndex) => queue.ToArray().CopyTo(array, arrayIndex);

        public bool Remove(string item)
        {
            var removed = false;
            var retained = new List<string>();

            while (queue.TryDequeue(out var current))
            {
                if (!removed && string.Equals(current, item, StringComparison.Ordinal))
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

        public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)queue.ToArray()).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
