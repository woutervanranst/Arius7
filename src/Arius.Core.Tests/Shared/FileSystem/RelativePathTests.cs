using Arius.Core.Shared.FileSystem;

namespace Arius.Core.Tests.Shared.FileSystem;

public class RelativePathTests
{
    [Test]
    public void Parse_ValidRelativePath_RendersCanonicalSlashPath()
    {
        var path = RelativePath.Parse("photos/2024/pic.jpg");

        path.ToString().ShouldBe("photos/2024/pic.jpg");
    }

    [Test]
    public void Root_RendersAsEmptyPath()
    {
        RelativePath.Root.ToString().ShouldBe(string.Empty);
    }

    [Test]
    public void Parse_RootedPath_Throws()
    {
        Should.Throw<FormatException>(() => RelativePath.Parse("/photos/pic.jpg"));
    }

    [Test]
    public void Parse_DotSegment_Throws()
    {
        Should.Throw<FormatException>(() => RelativePath.Parse("photos/../pic.jpg"));
    }

    [Test]
    public void FromPlatformRelativePath_NormalizesDirectorySeparators()
    {
        var path = RelativePath.FromPlatformRelativePath($"photos{Path.DirectorySeparatorChar}2024{Path.DirectorySeparatorChar}pic.jpg");

        path.ToString().ShouldBe("photos/2024/pic.jpg");
    }

    [Test]
    public void Name_ReturnsLastSegment()
    {
        RelativePath.Parse("photos/2024/pic.jpg").Name.ToString().ShouldBe("pic.jpg");
    }

    [Test]
    public void Parent_ReturnsContainingDirectory()
    {
        RelativePath.Parse("photos/2024/pic.jpg").Parent.ShouldBe(RelativePath.Parse("photos/2024"));
    }

    [Test]
    public void StartsWith_MatchingWholeSegment_ReturnsTrue()
    {
        RelativePath.Parse("photos/2024/pic.jpg").StartsWith(RelativePath.Parse("photos")).ShouldBeTrue();
    }

    [Test]
    public void StartsWith_PartialSegmentMatch_ReturnsFalse()
    {
        RelativePath.Parse("photoshop/pic.jpg").StartsWith(RelativePath.Parse("photos")).ShouldBeFalse();
    }

    [Test]
    public void SlashStringOperator_ComposesSingleSegments()
    {
        var path = RelativePath.Root / "photos" / "pic.jpg";

        path.ToString().ShouldBe("photos/pic.jpg");
    }

    [Test]
    public void SlashStringOperator_MultiSegmentAppend_Throws()
    {
        Should.Throw<FormatException>(() => _ = RelativePath.Root / "photos/pic.jpg");
    }

    [Test]
    public void SlashStringOperator_UnsafeAppend_Throws()
    {
        Should.Throw<FormatException>(() => _ = RelativePath.Root / ".."
        );
    }

    [Test]
    public void ToPointerPath_AppendsPointerSuffix()
    {
        RelativePath.Parse("photos/pic.jpg").ToPointerPath().ToString().ShouldBe("photos/pic.jpg.pointer.arius");
    }

    [Test]
    public void ToBinaryPath_RemovesPointerSuffix()
    {
        RelativePath.Parse("photos/pic.jpg.pointer.arius").ToBinaryPath().ToString().ShouldBe("photos/pic.jpg");
    }

    [Test]
    public void ToBinaryPath_NonPointerPath_Throws()
    {
        Should.Throw<InvalidOperationException>(() => RelativePath.Parse("photos/pic.jpg").ToBinaryPath());
    }

    [Test]
    public void ToBinaryPath_UppercasePointerSuffix_Throws()
    {
        Should.Throw<InvalidOperationException>(() => RelativePath.Parse("photos/pic.jpg.POINTER.ARIUS").ToBinaryPath());
    }

    [Test]
    public void IsPointerPath_ReturnsTrueOnlyForPointerSuffix()
    {
        RelativePath.Parse("photos/pic.jpg.pointer.arius").IsPointerPath().ShouldBeTrue();
        RelativePath.Parse("photos/pic.jpg.POINTER.ARIUS").IsPointerPath().ShouldBeFalse();
        RelativePath.Parse("photos/pic.jpg").IsPointerPath().ShouldBeFalse();
    }
}
