using System.Collections.Concurrent;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
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

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IBlobContainerService _blobs;
    private readonly IEncryptionService  _encryption;
    private readonly string              _l2Dir;

    // ── L1 LRU cache ──────────────────────────────────────────────────────────

    private sealed record L1Entry(string Prefix, Shard Shard, long Size);

    private readonly long                                        _l1BudgetBytes;
    private readonly LinkedList<L1Entry>                         _l1Lru = [];
    private readonly Dictionary<string, LinkedListNode<L1Entry>> _l1Map = new(StringComparer.Ordinal);
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
        _l2Dir         = RepositoryPaths.GetChunkIndexCacheDirectory(accountName, containerName);

        Directory.CreateDirectory(_l2Dir);
    }

    // ── Dedup lookup (tasks 4.6, 4.7) ─────────────────────────────────────────

    /// <summary>
    /// Batched dedup lookup: given a collection of content-hashes, returns the set
    /// that are already known (either from the tiered cache or the in-flight set).
    /// Hashes are grouped by shard prefix to amortize shard downloads.
    /// </summary>
    public async Task<IReadOnlyDictionary<ContentHash, ShardEntry>> LookupAsync(IEnumerable<ContentHash> contentHashes, CancellationToken cancellationToken = default)
    {
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
        if (_pendingEntries.IsEmpty) 
            return;

        // Group pending entries by shard prefix
        var byPrefix = _pendingEntries.GroupBy(e => Shard.PrefixOf(e.ContentHash));

        foreach (var group in byPrefix)
        {
            var prefix   = group.Key;
            var existing = await LoadShardAsync(prefix, cancellationToken);
            var merged   = existing.Merge(group);

            // Serialize and upload
            var bytes    = await ShardSerializer.SerializeAsync(merged, _encryption, cancellationToken);
            var blobName = BlobPaths.ChunkIndexShard(prefix);

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

            // Save to L2 disk cache (plaintext, no gzip/encryption)
            SaveToL2(prefix, merged);

            // Promote merged shard to L1
            PromoteToL1(prefix, merged, bytes.Length);
        }

        // Clear pending
        while (_pendingEntries.TryTake(out _))
        {
        }
    }

    // ── Tier resolution ───────────────────────────────────────────────────────

    private async Task<Shard> LoadShardAsync(string prefix, CancellationToken cancellationToken)
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
        var l2Path = Path.Combine(_l2Dir, prefix);
        if (File.Exists(l2Path))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(l2Path, cancellationToken);
                var shard = ShardSerializer.DeserializeLocal(bytes);
                PromoteToL1(prefix, shard, bytes.Length);
                return shard;
            }
            catch
            {
                // Stale or corrupt L2 file (e.g. old encrypted format) — treat as cache miss and fall through to L3.
                File.Delete(l2Path);
            }
        }

        // L3 (Azure)
        var blobName = BlobPaths.ChunkIndexShard(prefix);
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

        var loadedShard = ShardSerializer.Deserialize(downloaded, _encryption);
        SaveToL2(prefix, loadedShard);
        PromoteToL1(prefix, loadedShard, downloaded.Length);
        return loadedShard;
    }

    // ── L1 LRU management (task 4.4) ──────────────────────────────────────────

    private void PromoteToL1(string prefix, Shard shard, long approximateSizeBytes)
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

    private void SaveToL2(string prefix, Shard shard)
    {
        var path  = Path.Combine(_l2Dir, prefix);
        var bytes = ShardSerializer.SerializeLocal(shard);
        File.WriteAllBytes(path, bytes);
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
}
