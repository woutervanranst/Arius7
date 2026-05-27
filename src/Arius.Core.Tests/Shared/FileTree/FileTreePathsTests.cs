using Arius.Core.Shared.FileTree;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreePathsTests
{
    [Test]
    [Arguments("/photos")]
    [Arguments("/photos/2024")]
    [Arguments("C:/photos")]
    [Arguments("C:\\photos")]
    public void GetDirectoryId_RootedPath_Throws(string directoryPath)
    {
        Should.Throw<FormatException>(() => RelativePath.Parse(directoryPath));
    }

    [Test]
    public void GetDirectoryId_RelativePath_ReturnsStableSegment()
    {
        FileTreePaths.GetStagingDirectoryId(RelativePath.Parse("photos/2024"))
            .ShouldBe(PathSegment.Parse(HashCodec.ToLowerHex(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("photos/2024")))));
    }
}
