using Arius.Tests.Shared;

namespace Arius.Core.Tests.Shared.Storage;

public class BlobPathsTests
{
    [Test]
    public void ChunkPath_RendersCanonicalRelativePath()
    {
        var chunkHash = ChunkHash.Parse(new string('a', 64));

        BlobPaths.ChunkPath(chunkHash).ShouldBe(RelativePath.Parse($"chunks/{chunkHash}"));
    }

    [Test]
    public void ThinChunkPath_RendersCanonicalRelativePath()
    {
        var contentHash = ContentHash.Parse(new string('c', 64));

        BlobPaths.ThinChunkPath(contentHash).ShouldBe(RelativePath.Parse($"chunks/{contentHash}"));
    }

    [Test]
    public void ChunkRehydratedPath_RendersCanonicalRelativePath()
    {
        var chunkHash = ChunkHash.Parse(new string('d', 64));

        BlobPaths.ChunkRehydratedPath(chunkHash).ShouldBe(RelativePath.Parse($"chunks-rehydrated/{chunkHash}"));
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
        BlobPaths.ChunkIndexShardPath(PathSegment.Parse("ab"))
            .ShouldBe(RelativePath.Parse("chunk-index/ab"));
    }

    [Test]
    public void FileTreePath_RendersCanonicalRelativePath()
    {
        var fileTreeHash = FileTreeHash.Parse(new string('b', 64));

        BlobPaths.FileTreePath(fileTreeHash).ShouldBe(RelativePath.Parse($"filetrees/{fileTreeHash}"));
    }

    [Test]
    public void TestConvenienceOverloads_AppendLeafSegmentsWithoutHashParsing()
    {
        BlobPaths.ChunkPath("ow").ShouldBe(RelativePath.Parse("chunks/ow"));
        BlobPaths.FileTreePath("blob-0").ShouldBe(RelativePath.Parse("filetrees/blob-0"));
    }
}
