using Arius.Core.Features.StatsQuery;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using StatsQueryType = Arius.Core.Features.StatsQuery.StatsQuery;

namespace Arius.Core.Tests.Features.StatsQuery;

public class StatsQueryHandlerTests
{
    [Test]
    public async Task Handle_AggregatesManifestTotalsAndDistinctChunks()
    {
        var blobs = new FakeSeededBlobContainerService();
        var snapshot = new SnapshotManifest
        {
            Timestamp    = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
            RootHash     = FileTreeHashOf("root"),
            FileCount    = 3,
            TotalSize    = 600,
            AriusVersion = "test"
        };
        blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-stats-1", "ctr-stats-1", IEncryptionService.PlaintextInstance);

        // Two content hashes share one tar chunk 'a' (chunk_size 40), one large chunk 'b' (chunk_size 50).
        // Distinct chunks → 2; stored size → 40 + 50 = 90 (NOT 40 + 40 + 50).
        fixture.Index.AddEntry(new ShardEntry(ContentHashOf("a"), FakeChunkHash('a'), OriginalSize: 100, ChunkSize: 40, BlobTier.Cool));
        fixture.Index.AddEntry(new ShardEntry(ContentHashOf("b"), FakeChunkHash('a'), OriginalSize: 200, ChunkSize: 40, BlobTier.Cool));
        fixture.Index.AddEntry(new ShardEntry(ContentHashOf("c"), FakeChunkHash('b'), OriginalSize: 300, ChunkSize: 50, BlobTier.Cool));

        var handler = new StatsQueryHandler(fixture.Snapshot, fixture.Index, NullLogger<StatsQueryHandler>.Instance);
        var stats = await handler.Handle(new StatsQueryType(), CancellationToken.None);

        stats.Files.ShouldBe(3);
        stats.OriginalSize.ShouldBe(600);
        stats.UniqueChunks.ShouldBe(2);
        stats.StoredSize.ShouldBe(90);
    }

    [Test]
    public async Task Handle_NoSnapshot_ReturnsZeroes()
    {
        var blobs = new FakeSeededBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-stats-empty", "ctr-stats-empty", IEncryptionService.PlaintextInstance);

        var handler = new StatsQueryHandler(fixture.Snapshot, fixture.Index, NullLogger<StatsQueryHandler>.Instance);
        var stats = await handler.Handle(new StatsQueryType(), CancellationToken.None);

        stats.Files.ShouldBe(0);
        stats.OriginalSize.ShouldBe(0);
        stats.UniqueChunks.ShouldBe(0);
        stats.StoredSize.ShouldBe(0);
    }
}
