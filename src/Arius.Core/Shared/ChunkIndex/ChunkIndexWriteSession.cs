namespace Arius.Core.Shared.ChunkIndex;

internal sealed class ChunkIndexWriteSession
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ContentHash, ShardEntry> _sessionEntries = [];
    private readonly List<ShardEntry> _pendingEntries = [];
    private bool _flushInProgress;

    public bool TryLookup(ContentHash contentHash, out ShardEntry entry)
    {
        lock (_gate)
            return _sessionEntries.TryGetValue(contentHash, out entry!);
    }

    public void AddEntry(ShardEntry entry)
    {
        lock (_gate)
        {
            if (_flushInProgress)
                throw new InvalidOperationException("Cannot record chunk-index entries while a flush is in progress.");

            _sessionEntries[entry.ContentHash] = entry;
            _pendingEntries.Add(entry);
        }
    }

    public async Task FlushAsync(ChunkIndexShardCache shardCache, CancellationToken cancellationToken = default)
    {
        ShardEntry[] pendingSnapshot;
        lock (_gate)
        {
            if (_flushInProgress)
                throw new InvalidOperationException("Chunk-index flush is already in progress.");

            if (_pendingEntries.Count == 0)
                return;

            _flushInProgress = true;
            pendingSnapshot = [.. _pendingEntries];
        }

        try
        {
            var byPrefix = pendingSnapshot
                .GroupBy(e => Shard.PrefixOf(e.ContentHash))
                .Select(g => new KeyValuePair<PathSegment, ShardEntry[]>(g.Key, [.. g]))
                .ToArray();

            await Parallel.ForEachAsync(
                byPrefix,
                new ParallelOptions { MaxDegreeOfParallelism = ChunkIndexService.FlushWorkers, CancellationToken = cancellationToken },
                async (group, ct) => await shardCache.UpdateShardAsync(group.Key, group.Value, ct));

            lock (_gate)
            {
                _pendingEntries.Clear();
                _sessionEntries.Clear();
                _flushInProgress = false;
            }
        }
        catch
        {
            lock (_gate)
                _flushInProgress = false;

            throw;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pendingEntries.Clear();
            _sessionEntries.Clear();
            _flushInProgress = false;
        }
    }
}
