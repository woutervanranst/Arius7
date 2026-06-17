using Arius.Core.Shared.Hashes;

namespace Arius.Tests.Shared.Hashes;

public static class HashTestData
{
    public static ContentHash  FakeContentHash(char c)  => ContentHash.Parse(new string(c,  64));
    public static ChunkHash    FakeChunkHash(char c)    => ChunkHash.Parse(new string(c,    64));
    public static FileTreeHash FakeFileTreeHash(char c) => FileTreeHash.Parse(new string(c, 64));
}
