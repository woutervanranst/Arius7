namespace Arius.Core.Shared.ChunkIndex;

internal sealed class ChunkIndexReader(ChunkIndexShardCache shardCache)
{
    public async Task<ShardEntry?> LookupAsync(ContentHash contentHash, CancellationToken cancellationToken = default)
        => await shardCache.LookupAsync(contentHash, cancellationToken);

    public async Task<IReadOnlyDictionary<ContentHash, ShardEntry>> LookupAsync(
        IEnumerable<ContentHash> contentHashes,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<ContentHash, ShardEntry>();

        var byPrefix = contentHashes
            .GroupBy(Shard.PrefixOf)
            .ToArray();

        if (byPrefix.Length == 0)
            return result;

        foreach (var group in byPrefix)
        {
            var shard = await shardCache.GetShardAsync(group.Key, cancellationToken);
            foreach (var contentHash in group)
            {
                if (shard.TryLookup(contentHash, out var entry) && entry is not null)
                    result[contentHash] = entry;
            }
        }

        return result;
    }
}
