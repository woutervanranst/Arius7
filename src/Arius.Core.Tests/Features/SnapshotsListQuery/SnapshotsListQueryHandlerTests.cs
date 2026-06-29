using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
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
        var handler = new SnapshotsListQueryHandler(fixture.Snapshot, NullLogger<SnapshotsListQueryHandler>.Instance);

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
        var handler = new SnapshotsListQueryHandler(fixture.Snapshot, NullLogger<SnapshotsListQueryHandler>.Instance);

        var result = await handler.Handle(new SnapshotsListQueryType(), CancellationToken.None).ToListAsync();

        result.ShouldBeEmpty();
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
