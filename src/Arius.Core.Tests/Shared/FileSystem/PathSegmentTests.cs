using Arius.Core.Shared.FileSystem;

namespace Arius.Core.Tests.Shared.FileSystem;

public class PathSegmentTests
{
    [Test]
    public void Parse_ValidSegment_RendersOriginalValue()
    {
        PathSegment.Parse("photos").ToString().ShouldBe("photos");
    }

    [Test]
    public void Parse_MultiSegment_Throws()
    {
        Should.Throw<FormatException>(() => PathSegment.Parse("photos/2024"));
    }

    [Test]
    public void Parse_DotDot_Throws()
    {
        Should.Throw<FormatException>(() => PathSegment.Parse(".."));
    }
}
