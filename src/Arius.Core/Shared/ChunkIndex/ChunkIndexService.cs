using System.Collections.Concurrent;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// Three-tier chunk index cache.
///
/// L1: In-memory LRU (configurable byte budget, default 512 MB).
/// L2: Local disk at <c>~/.arius/{accountName}-{containerName}/chunk-index/</c>.
/// L3: Remote blob storage (download on miss, save to L2, promote to L1).
///
/// Dedup lookups are batched by shard prefix to amortize downloads.
/// An in-flight ConcurrentDictionary prevents duplicate uploads within one run.
/// </summary>
public sealed class ChunkIndexService : IDisposable
{
    // ── Configuration ─────────────────────────────────────────────────────────

    public const long DefaultCacheBudgetBytes = 512L * 1024 * 1024; // 512 MB

    internal const int ShardPrefixLength = 2;
    internal const int FlushWorkers = 8;
    internal static readonly RelativePath RepairInProgressMarkerPath = RelativePath.Root / PathSegment.Parse("chunk-index.repair-in-progress");

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IBlobContainerService _blobs;
    private readonly IEncryptionService  _encryption;
    private readonly RelativeFileSystem  _repositoryFileSystem;
    private readonly RelativeFileSystem  _l2FileSystem;

    // ── L1 LRU cache ──────────────────────────────────────────────────────────

    private sealed record L1Entry(PathSegment Prefix, Shard Shard, long Size);

    private readonly long                                        _l1BudgetBytes;
    private readonly LinkedList<L1Entry>                         _l1Lru = [];
    private readonly Dictionary<PathSegment, LinkedListNode<L1Entry>> _l1Map = [];
    private          long                                        _l1UsedBytes;
    private readonly Lock                                        _l1Lock = new();

    // ── In-flight set (task 4.8) ──────────────────────────────────────────────

    /// <summary>
    /// Content hashes that have been successfully determined as "already uploaded" or
    /// "just queued for upload" during this run, to prevent redundant uploads.
    /// </summary>
    private readonly ConcurrentDictionary<ContentHash, ShardEntry> _inFlight = [];

    // ── Pending new entries (collected during run, flushed at end) ────────────

    private readonly ConcurrentBag<ShardEntry> _pendingEntries = [];

    /// <summary>
    /// Initializes a ChunkIndexService and prepares the tiered chunk index cache state.
    /// </summary>
    /// <param name="blobs">Blob storage backend.</param>
    /// <param name="encryption">Encryption/hashing service.</param>
    /// <param name="accountName">Account name used to derive the on-disk L2 cache directory under the user's profile.</param>
    /// <param name="containerName">Container name used to derive the on-disk L2 cache directory under the user's profile.</param>
    /// <param name="cacheBudgetBytes">Approximate byte budget for the in-memory L1 cache.</param>
    /// <remarks>
    /// The constructor also ensures the L2 directory (derived from accountName and containerName) exists on disk.
    /// </remarks>
    public ChunkIndexService(
        IBlobContainerService blobs, 
        IEncryptionService encryption, 
        string accountName, 
        string containerName, 
        long cacheBudgetBytes = DefaultCacheBudgetBytes)
    {
        _blobs         = blobs;
        _encryption    = encryption;
        _l1BudgetBytes = cacheBudgetBytes;
        var repositoryRoot = RepositoryLocalStatePaths.GetRepositoryRoot(accountName, containerName);
        var l2Root = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(accountName, containerName);
        _repositoryFileSystem = new RelativeFileSystem(repositoryRoot);
        _l2FileSystem  = new RelativeFileSystem(l2Root);
        _repositoryFileSystem.CreateDirectory(RelativePath.Root);
        _l2FileSystem.CreateDirectory(RelativePath.Root);
    }

    // ── Dedup lookup (tasks 4.6, 4.7) ─────────────────────────────────────────

    /// <summary>
    /// Batched dedup lookup: given a collection of content-hashes, returns the set
    /// that are already known (either from the tiered cache or the in-flight set).
    /// Hashes are grouped by shard prefix to amortize shard downloads.
    /// </summary>
    public async Task<IReadOnlyDictionary<ContentHash, ShardEntry>> LookupAsync(IEnumerable<ContentHash> contentHashes, CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();

        var result = new Dictionary<ContentHash, ShardEntry>();

        // First pass: check in-flight set (no I/O)
        var remaining = new List<ContentHash>();
        foreach (var hash in contentHashes)
        {
            if (_inFlight.TryGetValue(hash, out var entry))
                result[hash] = entry;
            else
                remaining.Add(hash);
        }

        if (remaining.Count == 0) 
            return result;

        // Group remaining by shard prefix and resolve each prefix through tiers
        var byPrefix = remaining.GroupBy(Shard.PrefixOf);
        foreach (var group in byPrefix)
        {
            var shard = await LoadShardAsync(group.Key, cancellationToken);
            foreach (var hash in group)
            {
                if (shard.TryLookup(hash, out var entry) && entry is not null)
                    result[hash] = entry;
            }
        }

        return result;
    }

    public async Task<ShardEntry?> LookupAsync(ContentHash contentHash, CancellationToken cancellationToken = default)
    {
        var results = await LookupAsync([contentHash], cancellationToken);
        return results.GetValueOrDefault(contentHash);
    }

    // ── Record new entry ──────────────────────────────────────────────────────

    /// <summary>
    /// Records a newly uploaded chunk entry in the in-flight set and pending list.
    /// At end-of-run, call <see cref="FlushAsync"/> to persist all pending entries.
    /// </summary>
    public void AddEntry(ShardEntry entry)
    {
        ThrowIfRepairIncomplete();

        _inFlight[entry.ContentHash] = entry;
        _pendingEntries.Add(entry);
    }

    // ── Flush (upload shards at end of run) ───────────────────────────────────

    /// <summary>
    /// Merges all pending entries into existing shards and uploads changed shards.
    /// Should be called once at the end of an archive run.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();

        if (_pendingEntries.IsEmpty) 
            return;

        var byPrefix = _pendingEntries.GroupBy(e => Shard.PrefixOf(e.ContentHash)).ToList();

        await Parallel.ForEachAsync(
            byPrefix,
            new ParallelOptions { MaxDegreeOfParallelism = FlushWorkers, CancellationToken = cancellationToken },
            async (group, ct) => await FlushPrefixAsync(group.Key, group, ct));

        // Clear pending
        while (_pendingEntries.TryTake(out _))
        {
        }
    }

    // ── Tier resolution ───────────────────────────────────────────────────────

    private async Task<Shard> LoadShardAsync(PathSegment prefix, CancellationToken cancellationToken)
    {
        // L1 hit?
        lock (_l1Lock)
        {
            if (_l1Map.TryGetValue(prefix, out var node))
            {
                // Move to front (most recently used)
                _l1Lru.Remove(node);
                _l1Lru.AddFirst(node);
                return node.Value.Shard;
            }
        }

        // L2 hit?
        var l2Path = RelativePath.Root / prefix;
        if (_l2FileSystem.FileExists(l2Path))
        {
            try
            {
                var bytes = await _l2FileSystem.ReadAllBytesAsync(l2Path, cancellationToken);
                var shard = ShardSerializer.DeserializeLocal(bytes);
                PromoteToL1(prefix, shard, bytes.Length);
                return shard;
            }
            catch
            {
                // Stale or corrupt L2 file (e.g. old encrypted format) — treat as cache miss and fall through to L3.
                _l2FileSystem.DeleteFile(l2Path);
            }
        }

        // L3 (Azure)
        var blobName = BlobPaths.ChunkIndexShardPath(prefix);
        var meta     = await _blobs.GetMetadataAsync(blobName, cancellationToken);
        if (!meta.Exists)
        {
            // New prefix — empty shard
            var empty = new Shard();
            PromoteToL1(prefix, empty, 0);
            return empty;
        }

        await using var stream = await _blobs.DownloadAsync(blobName, cancellationToken);
        var             ms     = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        var downloaded = ms.ToArray();

        Shard loadedShard;
        try
        {
            loadedShard = ShardSerializer.Deserialize(downloaded, _encryption);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or IOException or UnauthorizedAccessException)
        {
            throw new ChunkIndexCorruptException(blobName, ex);
        }

        SaveToL2(prefix, loadedShard);
        PromoteToL1(prefix, loadedShard, downloaded.Length);
        return loadedShard;
    }

    private async Task FlushPrefixAsync(PathSegment prefix, IEnumerable<ShardEntry> entries, CancellationToken cancellationToken)
    {
        var existing = await LoadShardAsync(prefix, cancellationToken);
        var merged   = existing.Merge(entries);

        var bytes    = await ShardSerializer.SerializeAsync(merged, _encryption, cancellationToken);
        var blobName = BlobPaths.ChunkIndexShardPath(prefix);

        await _blobs.UploadAsync(
            blobName: blobName,
            content: new MemoryStream(bytes),
            metadata: new Dictionary<string, string>(),
            tier: BlobTier.Cool,
            contentType: _encryption.IsEncrypted
                ? ContentTypes.ChunkIndexGcmEncrypted
                : ContentTypes.ChunkIndexPlaintext,
            overwrite: true,
            cancellationToken: cancellationToken);

        SaveToL2(prefix, merged);
        PromoteToL1(prefix, merged, bytes.Length);
    }

    private void ThrowIfRepairIncomplete()
    {
        if (_repositoryFileSystem.FileExists(RepairInProgressMarkerPath))
            throw new ChunkIndexRepairIncompleteException(RepairInProgressMarkerPath);
    }

    // ── L1 LRU management (task 4.4) ──────────────────────────────────────────

    private void PromoteToL1(PathSegment prefix, Shard shard, long approximateSizeBytes)
    {
        lock (_l1Lock)
        {
            // Evict old entry for this prefix if present
            if (_l1Map.TryGetValue(prefix, out var existing))
            {
                _l1UsedBytes -= existing.Value.Size;
                _l1Lru.Remove(existing);
                _l1Map.Remove(prefix);
            }

            // Evict LRU entries until budget is satisfied
            while (_l1UsedBytes + approximateSizeBytes > _l1BudgetBytes && _l1Lru.Count > 0)
            {
                var lru = _l1Lru.Last!;
                _l1UsedBytes -= lru.Value.Size;
                _l1Map.Remove(lru.Value.Prefix);
                _l1Lru.RemoveLast();
            }

            // Add to front
            var node = _l1Lru.AddFirst(new L1Entry(prefix, shard, approximateSizeBytes));
            _l1Map[prefix] =  node;
            _l1UsedBytes   += approximateSizeBytes;
        }
    }

    // ── L2 disk write (task 4.5) ──────────────────────────────────────────────

    private void SaveToL2(PathSegment prefix, Shard shard)
    {
        var path  = RelativePath.Root / prefix;
        var bytes = ShardSerializer.SerializeLocal(shard);
        _l2FileSystem.WriteAllBytesAsync(path, bytes, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        /* future: flush in-progress state */
    }

    /// <summary>
    /// Clears the in-memory L1 LRU cache. Called by <see cref="FileTreeService"/> when a
    /// snapshot mismatch is detected, to ensure stale shard data is not served from memory
    /// after the L2 disk cache has been deleted.
    /// </summary>
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
        _l2FileSystem.ClearDirectory(RelativePath.Root);
        InvalidateL1();
    }

    public async Task<ChunkIndexRepairResult> RepairAsync(CancellationToken cancellationToken = default)
    {
        InvalidateL1();
        await _repositoryFileSystem.WriteAllBytesAsync(RepairInProgressMarkerPath, [], cancellationToken);
        _l2FileSystem.ClearDirectory(RelativePath.Root);

        var listedChunkCount = 0;
        var rebuiltEntryCount = 0;
        var rebuiltPrefixes = new HashSet<PathSegment>();

        await foreach (var item in _blobs.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: true, cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            listedChunkCount++;

            var entry = CreateRepairEntry(item);
            if (entry is null)
                continue;

            var prefix = Shard.PrefixOf(entry.ContentHash);
            var l2Path = RelativePath.Root / prefix;
            var shard = new Shard();
            if (_l2FileSystem.FileExists(l2Path))
            {
                var bytes = await _l2FileSystem.ReadAllBytesAsync(l2Path, cancellationToken);
                shard = ShardSerializer.DeserializeLocal(bytes);
            }

            SaveToL2(prefix, shard.Merge([entry]));
            rebuiltPrefixes.Add(prefix);
            rebuiltEntryCount++;
        }

        var uploadedShardCount = 0;
        foreach (var prefix in rebuiltPrefixes.OrderBy(prefix => prefix.ToString(), StringComparer.Ordinal))
        {
            var bytes = await _l2FileSystem.ReadAllBytesAsync(RelativePath.Root / prefix, cancellationToken);
            var shard = ShardSerializer.DeserializeLocal(bytes);
            var storageBytes = await ShardSerializer.SerializeAsync(shard, _encryption, cancellationToken);
            await _blobs.UploadAsync(
                BlobPaths.ChunkIndexShardPath(prefix),
                new MemoryStream(storageBytes),
                new Dictionary<string, string>(),
                BlobTier.Cool,
                _encryption.IsEncrypted ? ContentTypes.ChunkIndexGcmEncrypted : ContentTypes.ChunkIndexPlaintext,
                overwrite: true,
                cancellationToken: cancellationToken);
            uploadedShardCount++;
        }

        var deletedStaleShardCount = 0;
        await foreach (var item in _blobs.ListAsync(BlobPaths.ChunkIndexPrefix, cancellationToken: cancellationToken))
        {
            var prefix = item.Name.Name;
            if (rebuiltPrefixes.Contains(prefix))
                continue;

            await _blobs.DeleteAsync(item.Name, cancellationToken);
            deletedStaleShardCount++;
        }

        _repositoryFileSystem.DeleteFile(RepairInProgressMarkerPath);
        return new ChunkIndexRepairResult(listedChunkCount, rebuiltEntryCount, rebuiltPrefixes.Count, uploadedShardCount, deletedStaleShardCount);
    }

    private static ShardEntry? CreateRepairEntry(BlobListItem item)
    {
        if (!item.Metadata.TryGetValue(BlobMetadataKeys.AriusType, out var ariusType))
            return null;

        return ariusType switch
        {
            BlobMetadataKeys.TypeLarge => CreateLargeRepairEntry(item),
            BlobMetadataKeys.TypeThin => CreateThinRepairEntry(item),
            _ => null,
        };
    }

    private static ShardEntry CreateLargeRepairEntry(BlobListItem item)
    {
        var contentHash = ContentHash.Parse(item.Name.Name.ToString());
        var originalSize = ReadRequiredLongMetadata(item, BlobMetadataKeys.OriginalSize);
        var compressedSize = ReadChunkSize(item);
        return new ShardEntry(contentHash, ChunkHash.Parse(contentHash), originalSize, compressedSize);
    }

    private static ShardEntry CreateThinRepairEntry(BlobListItem item)
    {
        var contentHash = ContentHash.Parse(item.Name.Name.ToString());
        if (!item.Metadata.TryGetValue(BlobMetadataKeys.ParentChunkHash, out var parentChunkHashValue) || !ChunkHash.TryParse(parentChunkHashValue, out var parentChunkHash))
            throw new ChunkIndexRepairException(item.Name, $"missing or invalid {BlobMetadataKeys.ParentChunkHash} metadata");

        var originalSize = ReadRequiredLongMetadata(item, BlobMetadataKeys.OriginalSize);
        var compressedSize = ReadRequiredLongMetadata(item, BlobMetadataKeys.CompressedSize);
        return new ShardEntry(contentHash, parentChunkHash, originalSize, compressedSize);
    }

    private static long ReadChunkSize(BlobListItem item)
        => item.Metadata.TryGetValue(BlobMetadataKeys.ChunkSize, out var value) && long.TryParse(value, out var size)
            ? size
            : item.ContentLength ?? throw new ChunkIndexRepairException(item.Name, $"missing or invalid {BlobMetadataKeys.ChunkSize} metadata");

    private static long ReadRequiredLongMetadata(BlobListItem item, string key)
        => item.Metadata.TryGetValue(key, out var value) && long.TryParse(value, out var parsed)
            ? parsed
            : throw new ChunkIndexRepairException(item.Name, $"missing or invalid {key} metadata");
}
