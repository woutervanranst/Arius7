using Shouldly;

namespace Arius.Cli.Tests;

/// <summary>
/// Verifies <see cref="DisplayHelpers.RenderProgressBar"/> produces correct fill ratios.
/// </summary>
public class RenderProgressBarTests
{
    [Test]
    public void RenderProgressBar_ZeroFraction_AllEmpty()
    {
        var bar = DisplayHelpers.RenderProgressBar(0.0, 10);
        bar.ShouldContain(new string('░', 10));
    }

    [Test]
    public void RenderProgressBar_FullFraction_AllFilled()
    {
        var bar = DisplayHelpers.RenderProgressBar(1.0, 10);
        bar.ShouldContain(new string('█', 10));
    }

    [Test]
    public void RenderProgressBar_HalfFraction_HalfFilled()
    {
        var bar = DisplayHelpers.RenderProgressBar(0.5, 12);
        bar.ShouldContain(new string('█', 6));
        bar.ShouldContain(new string('░', 6));
    }

    [Test]
    public void RenderProgressBar_62Percent_Width12_SevenOrEightFilled()
    {
        var bar = DisplayHelpers.RenderProgressBar(0.62, 12);
        bar.ShouldContain(new string('█', 7));
        bar.ShouldContain(new string('░', 5));
    }

    [Test]
    public void RenderProgressBar_ClampsBelowZero()
    {
        var bar = DisplayHelpers.RenderProgressBar(-0.5, 8);
        bar.ShouldContain(new string('░', 8));
    }

    [Test]
    public void RenderProgressBar_ClampsAboveOne()
    {
        var bar = DisplayHelpers.RenderProgressBar(1.5, 8);
        bar.ShouldContain(new string('█', 8));
    }
}
