using System.Collections.Concurrent;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.ChunkIndex;

internal sealed class ChunkIndexShardCache(
    IBlobContainerService blobs,
    IEncryptionService encryption,
    RelativeFileSystem l2FileSystem,
    long l1BudgetBytes)
{
    private sealed record L1Entry(PathSegment Prefix, Shard Shard, long Size);

    private readonly ConcurrentDictionary<PathSegment, SemaphoreSlim> _prefixGates = [];
    private readonly LinkedList<L1Entry>                              _l1Lru       = [];
    private readonly Dictionary<PathSegment, LinkedListNode<L1Entry>> _l1Map       = [];
    private readonly Lock                                             _l1Lock      = new();
    private          long                                             _l1UsedBytes;

    public async Task<ShardEntry?> LookupAsync(ContentHash contentHash, CancellationToken cancellationToken = default)
    {
        var prefix = Shard.PrefixOf(contentHash);
        var gate = GetPrefixGate(prefix);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var shard = await LoadShardAsync(prefix, cancellationToken);
            return shard.TryLookup(contentHash, out var entry) ? entry : null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<ContentHash, ShardEntry>> LookupAsync(
        PathSegment prefix,
        IReadOnlyCollection<ContentHash> contentHashes,
        CancellationToken cancellationToken = default)
    {
        if (contentHashes.Count == 0)
            return new Dictionary<ContentHash, ShardEntry>();

        var gate = GetPrefixGate(prefix);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var shard = await LoadShardAsync(prefix, cancellationToken);
            var result = new Dictionary<ContentHash, ShardEntry>();
            foreach (var contentHash in contentHashes)
            {
                if (shard.TryLookup(contentHash, out var entry) && entry is not null)
                    result[contentHash] = entry;
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task UpdateShardAsync(PathSegment prefix, IReadOnlyCollection<ShardEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
            return;

        var gate = GetPrefixGate(prefix);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var shard = await LoadShardAsync(prefix, cancellationToken);
            shard.AddOrUpdateRange(entries);
            await StoreShardAsync(prefix, shard, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RebuildShardAsync(PathSegment prefix, IReadOnlyCollection<ShardEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
            return;

        var gate = GetPrefixGate(prefix);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var shard = new Shard();
            shard.AddOrUpdateRange(entries);
            await StoreShardAsync(prefix, shard, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public void InvalidateL1()
    {
        lock (_l1Lock)
        {
            _l1Lru.Clear();
            _l1Map.Clear();
            _l1UsedBytes = 0;
        }
    }

    public void InvalidateCaches()
    {
        l2FileSystem.ClearDirectory(RelativePath.Root);
        InvalidateL1();
    }

    private SemaphoreSlim GetPrefixGate(PathSegment prefix) => _prefixGates.GetOrAdd(prefix, static _ => new SemaphoreSlim(1, 1));

    private async Task<Shard> LoadShardAsync(PathSegment prefix, CancellationToken cancellationToken)
    {
        lock (_l1Lock)
        {
            if (_l1Map.TryGetValue(prefix, out var node))
            {
                _l1Lru.Remove(node);
                _l1Lru.AddFirst(node);
                return node.Value.Shard;
            }
        }

        var l2Path = RelativePath.Root / prefix;
        if (l2FileSystem.FileExists(l2Path))
        {
            try
            {
                var bytes = await l2FileSystem.ReadAllBytesAsync(l2Path, cancellationToken);
                var shard = ShardSerializer.DeserializeLocal(bytes);
                PromoteToL1(prefix, shard, bytes.Length);
                return shard;
            }
            catch
            {
                l2FileSystem.DeleteFile(l2Path);
            }
        }

        var blobName = BlobPaths.ChunkIndexShardPath(prefix);
        await using var stream = await blobs.TryDownloadAsync(blobName, cancellationToken);
        if (stream is null)
        {
            var empty = new Shard();
            PromoteToL1(prefix, empty, 0);
            return empty;
        }

        Shard loadedShard;
        try
        {
            loadedShard = ShardSerializer.Deserialize(stream, encryption);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
        {
            throw new ChunkIndexCorruptException(blobName, ex);
        }

        await SaveToL2Async(prefix, loadedShard, cancellationToken);
        PromoteToL1(prefix, loadedShard, stream.Position);
        return loadedShard;
    }

    private async Task StoreShardAsync(PathSegment prefix, Shard shard, CancellationToken cancellationToken)
    {
        var bytes = await ShardSerializer.SerializeAsync(shard, encryption, cancellationToken);
        await blobs.UploadAsync(
            BlobPaths.ChunkIndexShardPath(prefix),
            new MemoryStream(bytes),
            new Dictionary<string, string>(),
            BlobTier.Cool,
            encryption.IsEncrypted ? ContentTypes.ChunkIndexGcmEncrypted : ContentTypes.ChunkIndexPlaintext,
            overwrite: true,
            cancellationToken: cancellationToken);

        await SaveToL2Async(prefix, shard, cancellationToken);
        PromoteToL1(prefix, shard, bytes.Length);
    }

    private async Task SaveToL2Async(PathSegment prefix, Shard shard, CancellationToken cancellationToken)
    {
        var path = RelativePath.Root / prefix;
        var bytes = ShardSerializer.SerializeLocal(shard);
        await l2FileSystem.WriteAllBytesAsync(path, bytes, cancellationToken);
    }

    private void PromoteToL1(PathSegment prefix, Shard shard, long approximateSizeBytes)
    {
        lock (_l1Lock)
        {
            if (_l1Map.TryGetValue(prefix, out var existing))
            {
                _l1UsedBytes -= existing.Value.Size;
                _l1Lru.Remove(existing);
                _l1Map.Remove(prefix);
            }

            while (_l1UsedBytes + approximateSizeBytes > l1BudgetBytes && _l1Lru.Count > 0)
            {
                var lru = _l1Lru.Last!;
                _l1UsedBytes -= lru.Value.Size;
                _l1Map.Remove(lru.Value.Prefix);
                _l1Lru.RemoveLast();
            }

            var node = _l1Lru.AddFirst(new L1Entry(prefix, shard, approximateSizeBytes));
            _l1Map[prefix] = node;
            _l1UsedBytes += approximateSizeBytes;
        }
    }
}
