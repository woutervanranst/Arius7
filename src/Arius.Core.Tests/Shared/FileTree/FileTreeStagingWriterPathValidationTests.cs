namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreeStagingWriterPathValidationTests
{
    [Test]
    [Arguments(" /a.jpg")]
    [Arguments("photos/   /a.jpg")]
    public void RelativePathParse_WhitespaceOnlyDirectorySegment_Throws(string filePath)
    {
        Should.Throw<FormatException>(() => RelativePath.Parse(filePath));
    }

    [Test]
    [Arguments("dir/ ")]
    [Arguments(" ")]
    public void RelativePathParse_WhitespaceOnlyFileNameSegment_Throws(string filePath)
    {
        Should.Throw<FormatException>(() => RelativePath.Parse(filePath));
    }

    [Test]
    [Arguments("/photos/a.jpg")]
    [Arguments("C:/photos/a.jpg")]
    [Arguments("C:\\photos\\a.jpg")]
    [Arguments("photos//a.jpg")]
    [Arguments("photos/./a.jpg")]
    [Arguments("photos/../a.jpg")]
    [Arguments("photos\\a.jpg")]
    public void RelativePathParse_NonCanonicalPath_Throws(string filePath)
    {
        Should.Throw<FormatException>(() => RelativePath.Parse(filePath));
    }
}
