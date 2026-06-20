namespace Arius.Core.Shared.ChunkIndex;

/// <summary>
/// The shard a content hash routes to: either an existing shard blob (<see cref="Exists"/> is
/// <see langword="true"/>) or the terminal walk depth at which the hash's range is empty and a
/// new shard would be created.
/// </summary>
internal readonly record struct ShardTarget(PathSegment Prefix, bool Exists);

/// <summary>
/// Routing for the dynamic-depth chunk-index shard layout. The layout is self-describing from
/// shard blob existence: a shard named <c>aa</c> owns all hashes starting with <c>aa</c> unless
/// it was split into deeper shards (<c>aa0</c>..<c>aaf</c>, recursively, only non-empty ones
/// written). Routing never assumes a shard exists at the minimum depth.
/// </summary>
internal static class ChunkIndexRouter
{
    /// <summary>The fixed-depth root prefix every hash maps to (e.g. <c>aa</c>) — the listing and gating granularity.</summary>
    public static PathSegment GetRootPrefix(ContentHash contentHash)
        => PathSegment.Parse(contentHash.Prefix(ChunkIndexService.MinShardPrefixLength));

    /// <summary>
    /// All proper prefixes (length &gt;= <see cref="ChunkIndexService.MinShardPrefixLength"/>) of the given
    /// shard names: the depths at which a strictly-deeper shard exists. Built once per subtree so
    /// <see cref="ResolveTarget(IReadOnlySet{string}, IReadOnlySet{string}, ContentHash)"/> can test
    /// "does a descendant exist?" in O(1) instead of scanning every shard name at every depth.
    /// </summary>
    public static IReadOnlySet<string> BuildDescendantPrefixes(IReadOnlySet<string> existingShardNames)
    {
        var descendants = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in existingShardNames)
            for (var length = ChunkIndexService.MinShardPrefixLength; length < name.Length; length++)
                descendants.Add(name[..length]);
        return descendants;
    }

    /// <summary>
    /// Convenience overload that derives the descendant-prefix set on the fly; prefer the overload
    /// taking a precomputed set when resolving many hashes against the same subtree.
    /// </summary>
    public static ShardTarget ResolveTarget(IReadOnlySet<string> existingShardNames, ContentHash contentHash)
        => ResolveTarget(existingShardNames, BuildDescendantPrefixes(existingShardNames), contentHash);

    /// <summary>
    /// Resolves the shard for <paramref name="contentHash"/> against the set of existing shard
    /// names in its root subtree, using the parent-wins walk: the shallowest existing shard on the
    /// hash's prefix path is authoritative (an interrupted split leaves the parent intact, and the
    /// parent contains everything any published snapshot references). When no shard on the path
    /// exists, descend while any strictly deeper shard shares the prefix; the resulting depth is
    /// where the range is empty and where new entries for it would be written.
    /// <paramref name="descendantPrefixes"/> is <see cref="BuildDescendantPrefixes"/> of
    /// <paramref name="existingShardNames"/>.
    /// </summary>
    public static ShardTarget ResolveTarget(IReadOnlySet<string> existingShardNames, IReadOnlySet<string> descendantPrefixes, ContentHash contentHash)
    {
        var hex = contentHash.ToString();
        var length = ChunkIndexService.MinShardPrefixLength;
        while (true)
        {
            // Find the first match
            var prefix = hex[..length];
            if (existingShardNames.Contains(prefix))
                // Match
                return new ShardTarget(PathSegment.Parse(prefix), Exists: true);

            // A strictly-deeper shard descends from this prefix iff the prefix is a proper prefix of some name.
            if (!descendantPrefixes.Contains(prefix))
                // There will be no further matches: it doesnt exist yet
                return new ShardTarget(PathSegment.Parse(prefix), Exists: false);

            length++;
        }
    }

    /// <summary>
    /// Recursively partitions <paramref name="entries"/> (all within range of
    /// <paramref name="basePrefix"/>) into non-empty leaf shards of at most
    /// <paramref name="maxEntryCount"/> entries, splitting 16-way by the next hex character.
    /// </summary>
    public static IReadOnlyList<(PathSegment Prefix, IReadOnlyList<ShardEntry> Entries)> PartitionIntoLeaves(
        PathSegment basePrefix, IReadOnlyCollection<ShardEntry> entries, int maxEntryCount)
    {
        var leaves = new List<(PathSegment, IReadOnlyList<ShardEntry>)>();
        Recurse(basePrefix.ToString(), entries);
        return leaves;

        void Recurse(string prefix, IReadOnlyCollection<ShardEntry> scope)
        {
            foreach (var group in scope
                         .GroupBy(entry => entry.ContentHash.Prefix(prefix.Length + 1))
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var partition = group.ToList();
                if (partition.Count <= maxEntryCount)
                    leaves.Add((PathSegment.Parse(group.Key), partition));
                else
                    Recurse(group.Key, partition);
            }
        }
    }

    /// <summary>
    /// Inclusive 32-byte content-hash bounds for a hex prefix, for BLOB range queries
    /// (SQLite BLOB comparison is memcmp). Works for odd-nibble prefixes: <c>aa3</c> spans
    /// <c>aa30…00</c> through <c>aa3f…ff</c>.
    /// </summary>
    public static (byte[] Lower, byte[] Upper) GetHashRangeBounds(PathSegment prefix)
    {
        var hex = prefix.ToString();
        return (Convert.FromHexString(hex.PadRight(64, '0')), Convert.FromHexString(hex.PadRight(64, 'f')));
    }
}
