using Arius.Core.Shared.FileTree;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeStagingWriterTests
{
    [Test]
    public void GetDirectoryId_UsesCanonicalForwardSlashPath()
    {
        var id1 = FileTreeStagingPaths.GetDirectoryId("photos/2024");
        var id2 = FileTreeStagingPaths.GetDirectoryId("photos\\2024");

        id1.ShouldBe(id2);
        id1.Length.ShouldBe(64);
        id1.ShouldAllBe(c => Uri.IsHexDigit(c));
    }

    [Test]
    public void GetNodeDirectory_UsesTwoCharacterFanout()
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), "arius-staging-test");
        var dirId = FileTreeStagingPaths.GetDirectoryId("docs");

        var nodePath = FileTreeStagingPaths.GetNodeDirectory(stagingRoot, dirId);

        nodePath.ShouldBe(Path.Combine(stagingRoot, "dirs", dirId[..2], dirId));
    }
}
