using Arius.Core.Shared.Paths;

namespace Arius.Core.Tests.Shared;

public class RelativePathTests
{
    [Test]
    public void Root_IsExplicitAndEmptyWhenFormatted()
    {
        RelativePath.Root.IsRoot.ShouldBeTrue();
        RelativePath.Root.ToString().ShouldBe(string.Empty);
    }

    [Test]
    public void Parse_CanonicalPath_RoundTripsAndExposesNameAndParent()
    {
        var path = RelativePath.Parse("photos/2024/a.jpg");

        path.IsRoot.ShouldBeFalse();
        path.SegmentCount.ShouldBe(3);
        path.Name.ShouldBe(PathSegment.Parse("a.jpg"));
        path.Parent.ShouldBe(RelativePath.Parse("photos/2024"));
        path.ToString().ShouldBe("photos/2024/a.jpg");
    }

    [Test]
    public void Parse_AllowEmpty_ReturnsRoot()
    {
        RelativePath.Parse(string.Empty, allowEmpty: true).ShouldBe(RelativePath.Root);
    }

    [Test]
    public void Equality_IsOrdinalAndCaseSensitive()
    {
        var lower = RelativePath.Parse("photos/a.jpg");
        var upper = RelativePath.Parse("Photos/a.jpg");

        lower.ShouldNotBe(upper);
    }

    [Test]
    public void Append_ComposesPaths()
    {
        var path = RelativePath.Root / PathSegment.Parse("photos") / PathSegment.Parse("a.jpg");

        path.ToString().ShouldBe("photos/a.jpg");
    }

    [Test]
    public void FromPlatformRelativePath_NormalizesDirectorySeparators()
    {
        var path = RelativePath.FromPlatformRelativePath(@"photos\2024\a.jpg");

        path.ShouldBe(RelativePath.Parse("photos/2024/a.jpg"));
    }

    [Test]
    public void ToPlatformPath_JoinsWithRootDirectory()
    {
        var root = Path.Combine("C:", "repo");
        var path = RelativePath.Parse("photos/2024/a.jpg");

        path.ToPlatformPath(root).ShouldBe(Path.Combine(root, "photos", "2024", "a.jpg"));
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("/photos")]
    [Arguments("C:/photos")]
    [Arguments("photos//a.jpg")]
    [Arguments("photos/./a.jpg")]
    [Arguments("photos/../a.jpg")]
    [Arguments("photos\\a.jpg")]
    public void Parse_InvalidPath_ThrowsArgumentException(string value)
    {
        Should.Throw<ArgumentException>(() => RelativePath.Parse(value));
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments(@"\photos")]
    [Arguments(@"photos\\a.jpg")]
    public void FromPlatformRelativePath_InvalidPath_ThrowsArgumentException(string value)
    {
        Should.Throw<ArgumentException>(() => RelativePath.FromPlatformRelativePath(value));
    }
}
