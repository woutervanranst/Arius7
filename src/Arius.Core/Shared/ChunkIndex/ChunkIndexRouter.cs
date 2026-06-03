namespace Arius.Core.Shared.ChunkIndex;

internal static class ChunkIndexRouter
{
    public static PathSegment GetLeafPrefix(ContentHash contentHash) => Shard.PrefixOf(contentHash);
}
