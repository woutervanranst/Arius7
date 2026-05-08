using Arius.Core.Shared.Hashes;
using Arius.Tests.Shared;

namespace Arius.Core.Tests.Shared.Storage;

public class BlobPathStringsTests
{
    [Test]
    public void Helpers_RenderCanonicalBlobNames()
    {
        var chunkHash = ChunkHash.Parse(new string('a', 64));
        var contentHash = ContentHash.Parse(new string('b', 64));
        var fileTreeHash = FileTreeHash.Parse(new string('c', 64));

        BlobPathStrings.Chunk(chunkHash).ShouldBe($"chunks/{chunkHash}");
        BlobPathStrings.ThinChunk(contentHash).ShouldBe($"chunks/{contentHash}");
        BlobPathStrings.ChunkRehydrated(chunkHash).ShouldBe($"chunks-rehydrated/{chunkHash}");
        BlobPathStrings.FileTree(fileTreeHash).ShouldBe($"filetrees/{fileTreeHash}");
        BlobPathStrings.Snapshot("2024-06-15T100000.000Z").ShouldBe("snapshots/2024-06-15T100000.000Z");
        BlobPathStrings.ChunkIndexShard("ab").ShouldBe("chunk-index/ab");
    }
}
