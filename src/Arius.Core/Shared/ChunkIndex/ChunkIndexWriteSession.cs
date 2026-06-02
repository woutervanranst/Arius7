using System.Collections.Concurrent;

namespace Arius.Core.Shared.ChunkIndex;

internal sealed class ChunkIndexWriteSession
{
    private readonly ConcurrentDictionary<ContentHash, ShardEntry> _sessionEntries = [];
    private int _flushInProgress;

    public bool TryLookup(ContentHash contentHash, out ShardEntry entry)
        => _sessionEntries.TryGetValue(contentHash, out entry!);

    public void AddEntry(ShardEntry entry)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot record chunk-index entries while a flush is in progress.");

        _sessionEntries[entry.ContentHash] = entry;
    }

    public async Task FlushAsync(ChunkIndexShardCache shardCache, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _flushInProgress, 1) != 0)
            throw new InvalidOperationException("Chunk-index flush is already in progress.");

        try
        {
            if (_sessionEntries.IsEmpty)
                return;

            var byPrefix = _sessionEntries.Values
                .GroupBy(e => Shard.PrefixOf(e.ContentHash))
                .Select(g => new KeyValuePair<PathSegment, ShardEntry[]>(g.Key, [.. g]))
                .ToArray();

            await Parallel.ForEachAsync(
                byPrefix,
                new ParallelOptions { MaxDegreeOfParallelism = ChunkIndexService.FlushWorkers, CancellationToken = cancellationToken },
                async (group, ct) =>
                {
                    await shardCache.UpdateShardAsync(group.Key, group.Value, ct);
                });

            _sessionEntries.Clear();
        }
        finally
        {
            Volatile.Write(ref _flushInProgress, 0);
        }
    }

    public void Clear()
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot clear chunk-index entries while a flush is in progress.");

        _sessionEntries.Clear();
    }
}
