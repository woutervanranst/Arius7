using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// Distinct-chunk count and stored size for one storage tier (<see cref="BlobTier"/>).
/// </summary>
public sealed record ChunkTierStatistic(BlobTier Tier, long UniqueChunks, long StoredSize);

/// <summary>
/// Contract for resolving and publishing repository chunk-index entries. Feature handlers depend on this interface
/// so the concrete <see cref="ChunkIndexService"/> implementation can stay internal to Arius.Core.
/// </summary>
public interface IChunkIndexService : IDisposable
{
    /// <summary>
    /// Resolves chunk-index entries for the specified content hashes.
    /// </summary>
    internal Task<IReadOnlyDictionary<ContentHash, ShardEntry>> LookupAsync(IEnumerable<ContentHash> contentHashes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the chunk-index entry for a single content hash.
    /// </summary>
    internal Task<ShardEntry?> LookupAsync(ContentHash contentHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks loaded prefixes already validated against the current snapshot as valid for the newly published snapshot.
    /// </summary>
    internal Task PromoteToSnapshotVersionAsync(string newSnapshotVersion);

    /// <summary>
    /// Records a chunk-index entry as pending local flush state.
    /// </summary>
    internal void AddEntry(ShardEntry entry);

    /// <summary>
    /// Records multiple chunk-index entries as pending local flush state in a single transaction.
    /// </summary>
    internal void AddEntries(IEnumerable<ShardEntry> entries);

    /// <summary>
    /// Uploads pending local entries into remote shard blobs and marks flushed prefixes as synchronized remote-backed cache.
    /// </summary>
    internal Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the remote-backed local cache so later lookups revalidate prefixes from remote state.
    /// </summary>
    internal void InvalidateCaches();

    /// <summary>
    /// Rebuilds chunk-index shards from authoritative chunk blobs and deletes stale shard blobs.
    /// </summary>
    internal Task<ChunkIndexRepairResult> RepairAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregates distinct-chunk count and stored size per storage tier straight from the local
    /// chunk-index cache — no blob reads. Figures therefore reflect the cache's current coverage
    /// (entries loaded by browsing/lookups plus any not-yet-flushed pending entries) and finalise
    /// once the cache has fully synchronised.
    /// </summary>
    internal IReadOnlyList<ChunkTierStatistic> GetStatistics();
}
