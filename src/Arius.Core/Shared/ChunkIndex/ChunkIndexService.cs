using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// Three-tier chunk index cache.
///
/// L1: In-memory LRU for materialized shard pages (configurable byte budget, default 512 MB).
/// L2: Local disk at <c>~/.arius/{accountName}-{containerName}/chunk-index/</c>.
/// L3: Remote blob storage (download on miss, save to L2, promote to L1).
///
/// Dedup lookups are batched by shard prefix to amortize downloads.
/// Session entries make newly uploaded chunks visible to lookups before they are flushed to shards.
/// </summary>
public sealed class ChunkIndexService : IDisposable
{
    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Default byte budget for L1 materialized shard pages.
    /// This does not bound the session write buffer or pending flush entries.
    /// </summary>
    public const long DefaultL1CacheBudgetBytes = 512L * 1024 * 1024; // 512 MB

    internal const int ShardPrefixLength = 2;
    internal const int FlushWorkers = 8;
    internal static readonly RelativePath RepairInProgressMarkerPath = RelativePath.Root / PathSegment.Parse("chunk-index.repair-in-progress");

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly IBlobContainerService  _blobs;
    private readonly RelativeFileSystem     _repositoryFileSystem;
    private readonly ChunkIndexShardCache   _shardCache;
    private readonly ChunkIndexReader       _reader;
    private readonly ChunkIndexWriteSession _writeSession;

    /// <summary>
    /// Initializes a ChunkIndexService and prepares the tiered chunk index cache state.
    /// </summary>
    /// <param name="blobs">Blob storage backend.</param>
    /// <param name="encryption">Encryption/hashing service.</param>
    /// <param name="accountName">Account name used to derive the on-disk L2 cache directory under the user's profile.</param>
    /// <param name="containerName">Container name used to derive the on-disk L2 cache directory under the user's profile.</param>
    /// <param name="l1CacheBudgetBytes">Approximate byte budget for in-memory L1 shard pages.</param>
    /// <remarks>
    /// The constructor also ensures the L2 directory (derived from accountName and containerName) exists on disk.
    /// </remarks>
    public ChunkIndexService(
        IBlobContainerService blobs, 
        IEncryptionService encryption, 
        string accountName, 
        string containerName, 
        long l1CacheBudgetBytes = DefaultL1CacheBudgetBytes)
    {
        _blobs         = blobs;
        var repositoryRoot = RepositoryLocalStatePaths.GetRepositoryRoot(accountName, containerName);
        var l2Root         = RepositoryLocalStatePaths.GetChunkIndexCacheRoot(accountName, containerName);
        _repositoryFileSystem = new RelativeFileSystem(repositoryRoot);
        var l2FileSystem = new RelativeFileSystem(l2Root);
        _repositoryFileSystem.CreateDirectory(RelativePath.Root);
        l2FileSystem.CreateDirectory(RelativePath.Root);

        _shardCache = new ChunkIndexShardCache(blobs, encryption, l2FileSystem, l1CacheBudgetBytes);
        _reader = new ChunkIndexReader(_shardCache);
        _writeSession = new ChunkIndexWriteSession();
    }

    // ── Lookup ─────────────────────────────────────────

    /// <summary>
    /// Batched dedup lookup: given a collection of content-hashes, returns the set
    /// that are already known (either from the tiered cache or session entries).
    /// Shards are resolved through the shared L1/L2/L3 cache.
    /// Misses are omitted from the returned dictionary; an all-miss lookup returns
    /// an empty dictionary, not <c>null</c>.
    /// </summary>
    public async Task<IReadOnlyDictionary<ContentHash, ShardEntry>> LookupAsync(IEnumerable<ContentHash> contentHashes, CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();

        var result = new Dictionary<ContentHash, ShardEntry>();

        var misses = new List<ContentHash>();
        foreach (var hash in contentHashes)
        {
            if (_writeSession.TryLookup(hash, out var sessionEntry))
            {
                result[hash] = sessionEntry;
                continue;
            }

            misses.Add(hash);
        }

        var persistedHits = await _reader.LookupAsync(misses, cancellationToken);
        foreach (var (hash, entry) in persistedHits)
        {
            result[hash] = entry;
        }

        return result;
    }

    /// <summary>
    /// Looks up a single content hash and returns <c>null</c> when the hash is not
    /// known in either session entries or the persisted chunk-index shard.
    /// </summary>
    public async Task<ShardEntry?> LookupAsync(ContentHash contentHash, CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();

        if (_writeSession.TryLookup(contentHash, out var sessionEntry))
            return sessionEntry;

        return await _reader.LookupAsync(contentHash, cancellationToken);
    }

    // ── Record new entry ──────────────────────────────────────────────────────

    /// <summary>
    /// Records a newly uploaded chunk entry in session entries and the pending list.
    /// At end-of-run, call <see cref="FlushAsync"/> to persist all pending entries.
    /// </summary>
    public void AddEntry(ShardEntry entry)
    {
        ThrowIfRepairIncomplete();

        _writeSession.AddEntry(entry);
    }

    // ── Flush (upload shards at end of run) ───────────────────────────────────

    /// <summary>
    /// Merges all pending entries into existing shards and uploads changed shards.
    /// Should be called once at the end of an archive run.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfRepairIncomplete();

        await _writeSession.FlushAsync(_shardCache, cancellationToken);
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
        _shardCache.InvalidateL1();
    }

    public void InvalidateCaches()
    {
        _shardCache.InvalidateCaches();
    }

    // -- Repair

    public async Task<ChunkIndexRepairResult> RepairAsync(CancellationToken cancellationToken = default)
    {
        _shardCache.InvalidateL1();
        await AddRepairMarker();
        _shardCache.InvalidateCaches();

        var listedChunkCount = 0;
        var rebuiltEntryCount = 0;
        var entriesByPrefix = new Dictionary<PathSegment, List<ShardEntry>>();

        await foreach (var item in _blobs.ListAsync(BlobPaths.ChunksPrefix, includeMetadata: true, cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            listedChunkCount++;

            var entry = CreateRepairEntry(item);
            if (entry is null)
                continue;

            var prefix = Shard.PrefixOf(entry.ContentHash);
            if (!entriesByPrefix.TryGetValue(prefix, out var entries))
            {
                entries = [];
                entriesByPrefix[prefix] = entries;
            }

            entries.Add(entry);
            rebuiltEntryCount++;
        }

        var rebuiltPrefixes = entriesByPrefix.Keys.ToHashSet();

        var uploadedShardCount = 0;
        await Parallel.ForEachAsync(
            entriesByPrefix,
            new ParallelOptions { MaxDegreeOfParallelism = FlushWorkers, CancellationToken = cancellationToken },
            async (group, ct) =>
            {
                await _shardCache.RebuildShardAsync(group.Key, group.Value, ct);
                Interlocked.Increment(ref uploadedShardCount);
            });

        var deletedStaleShardCount = 0;
        await foreach (var item in _blobs.ListAsync(BlobPaths.ChunkIndexPrefix, cancellationToken: cancellationToken))
        {
            var prefix = item.Name.Name;
            if (rebuiltPrefixes.Contains(prefix))
                continue;

            await _blobs.DeleteAsync(item.Name, cancellationToken);
            deletedStaleShardCount++;
        }

        DeleteRepairMarker();
        _writeSession.Clear();

        return new ChunkIndexRepairResult(listedChunkCount, rebuiltEntryCount, rebuiltPrefixes.Count, uploadedShardCount, deletedStaleShardCount);
    }

    private static ShardEntry? CreateRepairEntry(BlobListItem item)
    {
        var metadata = item.Metadata ?? throw new ChunkIndexRepairException(item.Name, "metadata was not loaded for repair listing");
        if (!metadata.TryGetValue(BlobMetadataKeys.AriusType, out var ariusType))
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
        if (!item.Metadata!.TryGetValue(BlobMetadataKeys.ParentChunkHash, out var parentChunkHashValue) || !ChunkHash.TryParse(parentChunkHashValue, out var parentChunkHash))
            throw new ChunkIndexRepairException(item.Name, $"missing or invalid {BlobMetadataKeys.ParentChunkHash} metadata");

        var originalSize = ReadRequiredLongMetadata(item, BlobMetadataKeys.OriginalSize);
        var compressedSize = ReadRequiredLongMetadata(item, BlobMetadataKeys.CompressedSize);
        return new ShardEntry(contentHash, parentChunkHash, originalSize, compressedSize);
    }

    private static long ReadChunkSize(BlobListItem item)
        => item.Metadata is not null && item.Metadata.TryGetValue(BlobMetadataKeys.ChunkSize, out var value) && long.TryParse(value, out var size)
            ? size
            : item.ContentLength ?? throw new ChunkIndexRepairException(item.Name, $"missing or invalid {BlobMetadataKeys.ChunkSize} metadata");

    private static long ReadRequiredLongMetadata(BlobListItem item, string key)
        => item.Metadata is not null && item.Metadata.TryGetValue(key, out var value) && long.TryParse(value, out var parsed)
            ? parsed
            : throw new ChunkIndexRepairException(item.Name, $"missing or invalid {key} metadata");

    private void ThrowIfRepairIncomplete()
    {
        if (IsRepairMarker())
            throw new ChunkIndexRepairIncompleteException();
    }

    private       bool IsRepairMarker()  => _repositoryFileSystem.FileExists(RepairInProgressMarkerPath);
    private async Task AddRepairMarker() => await _repositoryFileSystem.WriteAllBytesAsync(RepairInProgressMarkerPath, [], CancellationToken.None);
    private       void DeleteRepairMarker() => _repositoryFileSystem.DeleteFile(RepairInProgressMarkerPath);
}
