namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// Public contract for the disk-backed chunk index. Feature handlers depend on this interface
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
    /// Promotes all loaded prefixes validated against the current snapshot version to the specified snapshot version.
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
    /// Uploads pending local shard state and marks the flushed prefixes as synchronized remote-backed cache.
    /// </summary>
    internal Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the remote-backed local cache so later lookups revalidate prefixes from remote state.
    /// </summary>
    internal void InvalidateCaches();

    /// <summary>
    /// Rebuilds the chunk index from chunk blobs and republishes the shard set.
    /// </summary>
    internal Task<ChunkIndexRepairResult> RepairAsync(CancellationToken cancellationToken = default);
}
