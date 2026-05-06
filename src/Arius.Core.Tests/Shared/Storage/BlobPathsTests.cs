using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Shared.Storage;

public class BlobPathsTests
{
    [Test]
    public void ChunkPath_RendersCanonicalRelativePath()
    {
        var chunkHash = ChunkHash.Parse(new string('a', 64));

        BlobPaths.ChunkPath(chunkHash).ShouldBe(RelativePath.Parse($"chunks/{chunkHash}"));
        BlobPaths.Chunk(chunkHash).ShouldBe($"chunks/{chunkHash}");
    }

    [Test]
    public void SnapshotPath_RendersCanonicalRelativePath()
    {
        BlobPaths.SnapshotPath("2024-06-15T100000.000Z")
            .ShouldBe(RelativePath.Parse("snapshots/2024-06-15T100000.000Z"));
    }

    [Test]
    public void ChunkIndexShardPath_RendersCanonicalRelativePath()
    {
        BlobPaths.ChunkIndexShardPath("ab")
            .ShouldBe(RelativePath.Parse("chunk-index/ab"));
    }

    [Test]
    public void FileTreePath_RendersCanonicalRelativePath()
    {
        var fileTreeHash = FileTreeHash.Parse(new string('b', 64));

        BlobPaths.FileTreePath(fileTreeHash).ShouldBe(RelativePath.Parse($"filetrees/{fileTreeHash}"));
        BlobPaths.FileTree(fileTreeHash).ShouldBe($"filetrees/{fileTreeHash}");
    }
}
