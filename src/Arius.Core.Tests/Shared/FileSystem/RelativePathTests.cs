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
    public void ToPointerPath_AppendsPointerSuffix()
    {
        RelativePath.Parse("photos/pic.jpg").ToPointerPath().ToString().ShouldBe("photos/pic.jpg.pointer.arius");
    }

    [Test]
    public void ToPointerPath_PointerPath_Throws()
    {
        Should.Throw<InvalidOperationException>(() => RelativePath.Parse("photos/pic.jpg.pointer.arius").ToPointerPath());
    }

    [Test]
    public void ToPointerPath_Root_Throws()
    {
        Should.Throw<InvalidOperationException>(() => RelativePath.Root.ToPointerPath());
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
    public void ToBinaryPath_UppercasePointerSuffix_RemovesPointerSuffix()
    {
        RelativePath.Parse("photos/pic.jpg.POINTER.ARIUS").ToBinaryPath().ToString().ShouldBe("photos/pic.jpg");
    }

    [Test]
    public void ToBinaryPath_RootEquivalentPointerPath_Throws()
    {
        Should.Throw<InvalidOperationException>(() => RelativePath.Parse(".pointer.arius").ToBinaryPath());
    }

    [Test]
    public void ToBinaryPath_DoubleSuffixPointerPath_Throws()
    {
        Should.Throw<InvalidOperationException>(() => RelativePath.Parse("photos/pic.jpg.pointer.arius.pointer.arius").ToBinaryPath());
    }

    [Test]
    public void IsPointerPath_ReturnsTrueForPointerSuffixIgnoringCase()
    {
        RelativePath.Parse("photos/pic.jpg.pointer.arius").IsPointerPath().ShouldBeTrue();
        RelativePath.Parse("photos/pic.jpg.POINTER.ARIUS").IsPointerPath().ShouldBeTrue();
        RelativePath.Parse("photos/pic.jpg").IsPointerPath().ShouldBeFalse();
    }

    [Test]
    public void Equals_UsesRequestedComparison()
    {
        RelativePath.Parse("Photos/Pic.jpg")
            .Equals(RelativePath.Parse("photos/pic.jpg"), StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue();
    }

    [Test]
    public void AppendSuffix_AppendsToFinalPathText()
    {
        RelativePath.Parse("photos/pic.jpg")
            .AppendSuffix(".pointer.arius")
            .ShouldBe(RelativePath.Parse("photos/pic.jpg.pointer.arius"));
    }

    [Test]
    public void RemoveSuffix_RemovesTrailingText()
    {
        RelativePath.Parse("photos/pic.jpg.pointer.arius")
            .RemoveSuffix(".pointer.arius", StringComparison.Ordinal)
            .ShouldBe(RelativePath.Parse("photos/pic.jpg"));
    }
}
