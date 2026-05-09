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

    [Test]
    public void Contains_UsesRequestedComparison()
    {
        PathSegment.Parse("VACATION.jpg")
            .Contains("vacation", StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue();
    }

    [Test]
    public void StartsWith_UsesRequestedComparison()
    {
        PathSegment.Parse("2026-03-22T150000.000Z")
            .StartsWith("2026-03", StringComparison.OrdinalIgnoreCase)
            .ShouldBeTrue();
    }

    [Test]
    public void EndsWith_UsesRequestedComparison()
    {
        PathSegment.Parse("photo.jpg.pointer.arius")
            .EndsWith(".pointer.arius", StringComparison.Ordinal)
            .ShouldBeTrue();
    }

    [Test]
    public void Compare_UsesProvidedComparer()
    {
        PathSegment.Parse("b.txt")
            .Compare(PathSegment.Parse("A.txt"), StringComparer.OrdinalIgnoreCase)
            .ShouldBeGreaterThan(0);
    }

    [Test]
    public void RemoveSuffix_RemovesTrailingContent()
    {
        PathSegment.Parse("photo.jpg.pointer.arius")
            .RemoveSuffix(".pointer.arius", StringComparison.Ordinal)
            .ShouldBe(PathSegment.Parse("photo.jpg"));
    }
}
