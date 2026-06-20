using Arius.Core.Shared.ChunkIndex;

namespace Arius.Core.Tests.Shared.ChunkIndex;

/// <summary>Test-only conveniences over <see cref="Shard"/>'s public surface (kept out of production code).</summary>
internal static class ShardTestExtensions
{
    /// <summary>Adds or replaces a batch of entries; duplicate content hashes use last-writer-wins order.</summary>
    public static void AddOrUpdateRange(this Shard shard, IEnumerable<ShardEntry> entries)
    {
        foreach (var entry in entries)
            shard.AddOrUpdate(entry);
    }

    /// <summary>Looks up an entry by content hash via the public <see cref="Shard.Entries"/> enumeration.</summary>
    public static bool TryLookup(this Shard shard, ContentHash contentHash, out ShardEntry? entry)
    {
        entry = shard.Entries.FirstOrDefault(e => e.ContentHash == contentHash);
        return entry is not null;
    }
}
