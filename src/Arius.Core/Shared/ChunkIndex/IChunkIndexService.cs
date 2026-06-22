using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.ChunkIndex;

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
    /// Resolves the chunk-index entry for a single content hash. Convenience overload of
    /// <see cref="LookupAsync(IEnumerable{ContentHash}, CancellationToken)"/>; prefer the batch overload
    /// when resolving multiple hashes.
    /// </summary>
    internal Task<ShardEntry?> LookupAsync(ContentHash contentHash, CancellationToken cancellationToken = default); // TODO no production callers — candidate for removal

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
    /// Aggregates repository-wide chunk-index figures from the local cache only — no blob reads. Figures
    /// reflect the cache's current coverage (entries loaded by browsing/lookups plus not-yet-flushed
    /// pending entries). Returns the deduplicated (uncompressed) original size over distinct content and
    /// the distinct-chunk count / stored size split by storage tier; chunk-level deduping is by chunk
    /// hash, since tar-bundled content hashes share one chunk.
    /// </summary>
    internal ChunkIndexStatistics GetStatistics();
}

/// <summary>
/// Repository-wide chunk-index aggregates (from the local cache only).
/// </summary>
/// <param name="DeduplicatedOriginalSize">
/// Sum of original (uncompressed) sizes over distinct content — the deduplicated, uncompressed size of
/// all unique data in the repository. Unlike <see cref="ChunkTierStatistic.StoredSize"/> this is
/// <em>before</em> compression, and unlike the snapshot's original size it counts each unique content
/// once (deduplicated).
/// </param>
/// <param name="ByTier">Distinct-chunk count and stored size split by storage tier.</param>
public sealed record ChunkIndexStatistics(long DeduplicatedOriginalSize, IReadOnlyList<ChunkTierStatistic> ByTier);

/// <summary>
/// Distinct-chunk count and stored size for one storage tier (<see cref="BlobTier"/>).
/// </summary>
public sealed record ChunkTierStatistic(BlobTier Tier, long UniqueChunks, long StoredSize);