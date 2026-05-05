using Arius.Core.Shared.Paths;

namespace Arius.Core.Tests.Shared;

public class PathSegmentTests
{
    [Test]
    [Arguments("photos")]
    [Arguments("2024 trip")]
    [Arguments(" report.pdf ")]
    public void Parse_ValidSegment_RoundTrips(string value)
    {
        var segment = SegmentOf(value);

        segment.ToString().ShouldBe(value);
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments("photos/2024")]
    [Arguments("photos\\2024")]
    [Arguments("photos\r")]
    [Arguments("photos\n")]
    [Arguments("photos\0")]
    public void Parse_InvalidSegment_ThrowsArgumentException(string value)
    {
        Should.Throw<ArgumentException>(() => SegmentOf(value));
    }

    [Test]
    public void Parse_NullSegment_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => SegmentOf(null!));
    }

    [Test]
    public void Equals_WithStringComparison_SupportsOrdinalIgnoreCase()
    {
        var lower = SegmentOf("docs");
        var upper = SegmentOf("Docs");

        lower.Equals(upper, StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        lower.Equals(upper, StringComparison.Ordinal).ShouldBeFalse();
    }

    [Test]
    public void OrdinalIgnoreCaseComparer_TreatsDifferingCasingAsSameElement()
    {
        var segments = new HashSet<PathSegment>(PathSegment.OrdinalIgnoreCaseComparer)
        {
            SegmentOf("docs")
        };

        segments.Add(SegmentOf("Docs")).ShouldBeFalse();
    }

    [Test]
    public void Extension_ReturnsSuffixIncludingLeadingDot()
    {
        var segment = SegmentOf("report.tar.gz");

        segment.Extension.ShouldBe(".gz");
    }
}
