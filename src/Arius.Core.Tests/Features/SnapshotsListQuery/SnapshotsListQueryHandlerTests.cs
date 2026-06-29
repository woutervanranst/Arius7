using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using SnapshotsListQueryType = Arius.Core.Features.SnapshotsListQuery.SnapshotsListQuery;

namespace Arius.Core.Tests.Features.SnapshotsListQuery;

public class SnapshotsListQueryHandlerTests
{
    [Test]
    public async Task Handle_MultipleSnapshots_StreamsOldestToNewestWithVersionTimestampAndFileCount()
    {
        var blobs = new FakeSeededBlobContainerService();
        var s1 = await SeedSnapshotAsync(blobs, new DateTimeOffset(2024, 1, 10, 8, 0, 0, TimeSpan.Zero), fileCount: 100);
        var s3 = await SeedSnapshotAsync(blobs, new DateTimeOffset(2024, 3, 12, 9, 14, 0, TimeSpan.Zero), fileCount: 142);
        var s2 = await SeedSnapshotAsync(blobs, new DateTimeOffset(2024, 2, 2, 23, 40, 0, TimeSpan.Zero), fileCount: 120);

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-snap-1", "ctr-snap-1", IEncryptionService.PlaintextInstance);
        var handler = new SnapshotsListQueryHandler(fixture.Snapshot, new FakeLogger<SnapshotsListQueryHandler>());

        var result = await handler.Handle(new SnapshotsListQueryType(), CancellationToken.None).ToListAsync();

        result.Count.ShouldBe(3);
        result.Select(s => s.Timestamp).ShouldBe([s1, s2, s3]); // oldest → newest
        result.Select(s => s.FileCount).ShouldBe([100, 120, 142]);

        foreach (var snapshot in result)
        {
            var resolved = await fixture.Snapshot.ResolveAsync(snapshot.Version, CancellationToken.None);
            resolved.ShouldNotBeNull();
            resolved!.Timestamp.ShouldBe(snapshot.Timestamp);
        }
    }

    [Test]
    public async Task Handle_NoSnapshots_StreamsEmpty()
    {
        var blobs = new FakeSeededBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-snap-empty", "ctr-snap-empty", IEncryptionService.PlaintextInstance);
        var handler = new SnapshotsListQueryHandler(fixture.Snapshot, new FakeLogger<SnapshotsListQueryHandler>());

        var result = await handler.Handle(new SnapshotsListQueryType(), CancellationToken.None).ToListAsync();

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task Handle_UnresolvableManifest_IsLoggedAndSkipped()
    {
        // A blob is listed but its manifest fails to resolve (returns null) — the handler must log a
        // warning and skip it, still streaming the resolvable ones. The real SnapshotService never
        // returns null for a name it just listed, so this branch is exercised with a substitute.
        var blob1 = BlobPaths.SnapshotPath(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var blob2 = BlobPaths.SnapshotPath(new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero));

        var snapshots = Substitute.For<ISnapshotService>();
        snapshots.ListBlobNamesAsync(Arg.Any<CancellationToken>()).Returns(new[] { blob1, blob2 });
        snapshots.GetVersion(blob1).Returns("2024-01-01T000000.000Z");
        snapshots.GetVersion(blob2).Returns("2024-02-01T000000.000Z");
        snapshots.ResolveAsync("2024-01-01T000000.000Z", Arg.Any<CancellationToken>()).Returns((SnapshotManifest?)null);
        snapshots.ResolveAsync("2024-02-01T000000.000Z", Arg.Any<CancellationToken>()).Returns(new SnapshotManifest
        {
            Timestamp    = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero),
            RootHash     = FileTreeHashOf("root-2"),
            FileCount    = 7,
            OriginalSize = 0,
            AriusVersion = "test"
        });

        var logger  = new FakeLogger<SnapshotsListQueryHandler>();
        var handler = new SnapshotsListQueryHandler(snapshots, logger);

        var result = await handler.Handle(new SnapshotsListQueryType(), CancellationToken.None).ToListAsync();

        result.Count.ShouldBe(1); // the unresolvable snapshot is skipped
        result[0].Version.ShouldBe("2024-02-01T000000.000Z");
        result[0].FileCount.ShouldBe(7);
        logger.Collector.GetSnapshot().ShouldContain(r => r.Level == LogLevel.Warning && r.Message.Contains("could not be resolved"));
    }

    private static async Task<DateTimeOffset> SeedSnapshotAsync(FakeSeededBlobContainerService blobs, DateTimeOffset timestamp, long fileCount)
    {
        var manifest = new SnapshotManifest
        {
            Timestamp    = timestamp,
            RootHash     = FileTreeHashOf($"root-{timestamp:O}"),
            FileCount    = fileCount,
            OriginalSize = fileCount * 1000,
            AriusVersion = "test"
        };
        blobs.AddBlob(BlobPaths.SnapshotPath(timestamp), await SnapshotSerializer.SerializeAsync(manifest, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance));
        return timestamp;
    }
}
