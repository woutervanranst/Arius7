using Arius.Core.Features.LatestSnapshotVersionQuery;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using LatestSnapshotVersionQueryType = Arius.Core.Features.LatestSnapshotVersionQuery.LatestSnapshotVersionQuery;

namespace Arius.Core.Tests.Features.LatestSnapshotVersionQuery;

public class LatestSnapshotVersionQueryHandlerTests
{
    [Test]
    public async Task Handle_MultipleSnapshots_ReturnsTheNewestSnapshotsVersion()
    {
        var blobs  = new FakeSeededBlobContainerService();
        var newest = new DateTimeOffset(2024, 3, 12, 9, 14, 0, TimeSpan.Zero);
        await SeedSnapshotAsync(blobs, new DateTimeOffset(2024, 1, 10, 8, 0, 0, TimeSpan.Zero));
        await SeedSnapshotAsync(blobs, newest);
        await SeedSnapshotAsync(blobs, new DateTimeOffset(2024, 2, 2, 23, 40, 0, TimeSpan.Zero));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-latest-1", "ctr-latest-1", IEncryptionService.PlaintextInstance);
        var handler = new LatestSnapshotVersionQueryHandler(fixture.Snapshot);

        var version = await handler.Handle(new LatestSnapshotVersionQueryType(), CancellationToken.None);

        version.ShouldNotBeNull();
        (await fixture.Snapshot.ResolveAsync(version, CancellationToken.None))!.Timestamp.ShouldBe(newest);
    }

    [Test]
    public async Task Handle_NoSnapshots_ReturnsNull()
    {
        var blobs = new FakeSeededBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-latest-empty", "ctr-latest-empty", IEncryptionService.PlaintextInstance);
        var handler = new LatestSnapshotVersionQueryHandler(fixture.Snapshot);

        var version = await handler.Handle(new LatestSnapshotVersionQueryType(), CancellationToken.None);

        version.ShouldBeNull();
    }

    private static async Task SeedSnapshotAsync(FakeSeededBlobContainerService blobs, DateTimeOffset timestamp)
    {
        var manifest = new SnapshotManifest
        {
            Timestamp    = timestamp,
            RootHash     = FileTreeHashOf($"root-{timestamp:O}"),
            FileCount    = 1,
            OriginalSize = 1000,
            AriusVersion = "test"
        };
        blobs.AddBlob(BlobPaths.SnapshotPath(timestamp), await SnapshotSerializer.SerializeAsync(manifest, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance));
    }
}
