using Arius.Cli.Commands.Snapshot;
using Arius.Core.Features.SnapshotsListQuery;

namespace Arius.Cli.Tests.Commands.Snapshot;

public class SnapshotArgumentResolverTests
{
    private static readonly IReadOnlyList<SnapshotInfo> Snapshots =
    [
        new("2024-01-01T000000.000Z", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 1),
        new("2024-02-01T000000.000Z", new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero), 2),
        new("2024-03-01T000000.000Z", new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero), 3),
    ];

    [Test]
    public void Resolve_Index_ReturnsVersionByOneBasedPosition()
    {
        SnapshotArgumentResolver.Resolve("1", Snapshots).ShouldBe("2024-01-01T000000.000Z");
        SnapshotArgumentResolver.Resolve("3", Snapshots).ShouldBe("2024-03-01T000000.000Z");
    }

    [Test]
    public void Resolve_IndexOutOfRange_Throws()
    {
        Should.Throw<ArgumentException>(() => SnapshotArgumentResolver.Resolve("0", Snapshots));
        Should.Throw<ArgumentException>(() => SnapshotArgumentResolver.Resolve("4", Snapshots));
    }

    [Test]
    public void Resolve_TimestampWithColons_StripsColonsToVersionPrefix()
    {
        SnapshotArgumentResolver.Resolve("2024-04-02T13:09:54", Snapshots).ShouldBe("2024-04-02T130954");
    }

    [Test]
    public void Resolve_PartialOrStoredFormat_ReturnedVerbatim()
    {
        SnapshotArgumentResolver.Resolve("2024-04-02", Snapshots).ShouldBe("2024-04-02");
        SnapshotArgumentResolver.Resolve("2024-02-01T000000.000Z", Snapshots).ShouldBe("2024-02-01T000000.000Z");
    }
}
