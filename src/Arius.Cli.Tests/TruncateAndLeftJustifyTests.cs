namespace Arius.Cli.Tests;

/// <summary>
/// Verifies <see cref="DisplayHelpers.TruncateAndLeftJustify"/> edge cases.
/// </summary>
public class TruncateAndLeftJustifyTests
{
    [Test]
    public void ShortPath_PaddedToWidth()
    {
        var result = DisplayHelpers.TruncateAndLeftJustify("hi.txt", 10);
        result.ShouldBe("hi.txt    ");
        result.Length.ShouldBe(10);
    }

    [Test]
    public void ExactWidthPath_NotPadded()
    {
        var result = DisplayHelpers.TruncateAndLeftJustify("12345", 5);
        result.ShouldBe("12345");
        result.Length.ShouldBe(5);
    }

    [Test]
    public void LongPath_TruncatedWithEllipsisPrefix()
    {
        var result = DisplayHelpers.TruncateAndLeftJustify("abcdefghij", 7);
        result.ShouldBe("...ghij");
        result.Length.ShouldBe(7);
    }

    [Test]
    public void Width4_LongPath_EllipsisPlusOneChar()
    {
        var result = DisplayHelpers.TruncateAndLeftJustify("abcde", 4);
        result.ShouldBe("...e");
        result.Length.ShouldBe(4);
    }

    [Test]
    public void EmptyString_PaddedToWidth()
    {
        var result = DisplayHelpers.TruncateAndLeftJustify("", 5);
        result.ShouldBe("     ");
        result.Length.ShouldBe(5);
    }
}
