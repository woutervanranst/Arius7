using Arius.Core.Shared;

namespace Arius.Core.Tests.Shared;

public class RepositoryRelativePathTests
{
    [Test]
    [Arguments("photos/a.jpg")]
    [Arguments("photos/2024/a.jpg")]
    [Arguments(" photos/a.jpg ")]
    public void ValidateCanonical_ValidRelativePath_DoesNotThrow(string path)
    {
        Should.NotThrow(() => RepositoryRelativePath.ValidateCanonical(path));
    }

    [Test]
    public void ValidateCanonical_AllowEmptyRootSentinel_DoesNotThrow()
    {
        Should.NotThrow(() => RepositoryRelativePath.ValidateCanonical(string.Empty, allowEmpty: true));
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("/photos")]
    [Arguments("/photos/a.jpg")]
    [Arguments("C:/photos/a.jpg")]
    [Arguments("C:\\photos\\a.jpg")]
    [Arguments("photos\\a.jpg")]
    [Arguments("photos//a.jpg")]
    [Arguments("photos/./a.jpg")]
    [Arguments("photos/../a.jpg")]
    [Arguments("photos/\r/a.jpg")]
    [Arguments("photos/\n/a.jpg")]
    [Arguments("photos/\0/a.jpg")]
    [Arguments("photos/   /a.jpg")]
    public void ValidateCanonical_InvalidPath_ThrowsArgumentException(string path)
    {
        Should.Throw<ArgumentException>(() => RepositoryRelativePath.ValidateCanonical(path));
    }

    [Test]
    public void ValidateCanonical_EmptyWithoutAllowEmpty_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => RepositoryRelativePath.ValidateCanonical(string.Empty));
    }
}
