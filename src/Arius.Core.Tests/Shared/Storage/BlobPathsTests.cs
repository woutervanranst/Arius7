using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Shared.Storage;

public class BlobPathsTests
{
    [Test]
    public void ThinChunk_UsesThinContentHashPath()
    {
        BlobPaths.ThinChunk(FakeContentHash('a')).ShouldBe($"{BlobPaths.Chunks}{FakeContentHash('a')}");
    }
}
