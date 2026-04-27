using Arius.Core.Shared.Hashes;

namespace Arius.Tests.Shared.Hashes;

internal static class HashTestData
{
    public static ContentHash Content(char c) => ContentHash.Parse(new string(c, 64));

    public static ChunkHash Chunk(char c) => ChunkHash.Parse(new string(c, 64));

    public static FileTreeHash FileTree(char c) => FileTreeHash.Parse(new string(c, 64));
}
