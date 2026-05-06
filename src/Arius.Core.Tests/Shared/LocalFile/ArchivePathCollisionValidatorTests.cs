using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.LocalFile;

namespace Arius.Core.Tests.Shared.LocalFile;

public class ArchivePathCollisionValidatorTests
{
    [Test]
    public void Validate_NoCollision_DoesNotThrow()
    {
        var pairs = new[]
        {
            new FilePair { Path = RelativePath.Parse("photos/pic.jpg"), Binary = CreateBinary("photos/pic.jpg"), Pointer = null },
            new FilePair { Path = RelativePath.Parse("photos/pic2.jpg"), Binary = CreateBinary("photos/pic2.jpg"), Pointer = null }
        };

        ArchivePathCollisionValidator.Validate(pairs);
    }

    [Test]
    public void Validate_CaseInsensitiveCollision_Throws()
    {
        var pairs = new[]
        {
            new FilePair { Path = RelativePath.Parse("photos/pic.jpg"), Binary = CreateBinary("photos/pic.jpg"), Pointer = null },
            new FilePair { Path = RelativePath.Parse("Photos/pic.jpg"), Binary = CreateBinary("Photos/pic.jpg"), Pointer = null }
        };

        var exception = Should.Throw<InvalidOperationException>(() => ArchivePathCollisionValidator.Validate(pairs));
        exception.Message.ShouldContain("photos/pic.jpg");
        exception.Message.ShouldContain("Photos/pic.jpg");
    }

    private static BinaryFile CreateBinary(string path) => new()
    {
        Path = RelativePath.Parse(path),
        Size = 1,
        Created = DateTimeOffset.UnixEpoch,
        Modified = DateTimeOffset.UnixEpoch
    };
}
