using Arius.Core.Features.StatisticsQuery;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Snapshot;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using StatisticsQueryType = Arius.Core.Features.StatisticsQuery.StatisticsQuery;

namespace Arius.Core.Tests.Features.StatisticsQuery;

public class StatisticsQueryHandlerTests
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
            OriginalSize    = 1000, // per-snapshot logical size; deliberately ≠ the repo-wide deduplicated sum (600)
            AriusVersion = "test"
        };
        blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-stats-1", "ctr-stats-1", IEncryptionService.PlaintextInstance);

        // Two content hashes share one tar chunk 'a' (chunk_size 40), one large chunk 'b' (chunk_size 50).
        // Distinct chunks → 2; stored size → 40 + 50 = 90 (NOT 40 + 40 + 50).
        fixture.Index.AddEntry(new ShardEntry(ContentHashOf("a"), FakeChunkHash('a'), OriginalSize: 100, ChunkSize: 40, BlobTier.Cool));
        fixture.Index.AddEntry(new ShardEntry(ContentHashOf("b"), FakeChunkHash('a'), OriginalSize: 200, ChunkSize: 40, BlobTier.Cool));
        fixture.Index.AddEntry(new ShardEntry(ContentHashOf("c"), FakeChunkHash('b'), OriginalSize: 300, ChunkSize: 50, BlobTier.Cool));

        var handler = new StatisticsQueryHandler(fixture.Snapshot, fixture.Index, NullLogger<StatisticsQueryHandler>.Instance);
        var stats = await handler.Handle(new StatisticsQueryType(), CancellationToken.None);

        stats.Files.ShouldBe(3);
        stats.OriginalSize.ShouldBe(1000); // from the manifest, independent of the chunk index
        stats.DeduplicatedSize.ShouldBe(600); // distinct content a+b+c = 100+200+300 (uncompressed); proves it is NOT the manifest's OriginalSize
        stats.UniqueChunks.ShouldBe(2);
        stats.StoredSize.ShouldBe(90);

        // All chunks are Cool → a single tier row carrying the full distinct-chunk aggregate.
        stats.StoredByTier.Count.ShouldBe(1);
        stats.StoredByTier[0].Tier.ShouldBe(BlobTier.Cool);
        stats.StoredByTier[0].UniqueChunks.ShouldBe(2);
        stats.StoredByTier[0].StoredSize.ShouldBe(90);
    }

    [Test]
    public async Task Handle_SplitsStoredSizeByStorageTier()
    {
        var blobs = new FakeSeededBlobContainerService();
        var snapshot = new SnapshotManifest
        {
            Timestamp    = new DateTimeOffset(2026, 3, 22, 15, 0, 0, TimeSpan.Zero),
            RootHash     = FileTreeHashOf("root"),
            FileCount    = 2,
            OriginalSize    = 500,
            AriusVersion = "test"
        };
        blobs.AddBlob(BlobPaths.SnapshotPath(snapshot.Timestamp), await SnapshotSerializer.SerializeAsync(snapshot, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance));

        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-stats-tier", "ctr-stats-tier", IEncryptionService.PlaintextInstance);

        // Two chunks in distinct tiers: chunk 'a' (40) Cool, chunk 'b' (60) Archive.
        fixture.Index.AddEntry(new ShardEntry(ContentHashOf("a"), FakeChunkHash('a'), OriginalSize: 100, ChunkSize: 40, BlobTier.Cool));
        fixture.Index.AddEntry(new ShardEntry(ContentHashOf("b"), FakeChunkHash('b'), OriginalSize: 400, ChunkSize: 60, BlobTier.Archive));

        var handler = new StatisticsQueryHandler(fixture.Snapshot, fixture.Index, NullLogger<StatisticsQueryHandler>.Instance);
        var stats = await handler.Handle(new StatisticsQueryType(), CancellationToken.None);

        stats.UniqueChunks.ShouldBe(2);
        stats.StoredSize.ShouldBe(100);
        stats.DeduplicatedSize.ShouldBe(500); // distinct content a+b = 100+400 (uncompressed)

        // Ordered by serialized tier (Cool=2 before Archive=4).
        stats.StoredByTier.Count.ShouldBe(2);
        stats.StoredByTier[0].Tier.ShouldBe(BlobTier.Cool);
        stats.StoredByTier[0].StoredSize.ShouldBe(40);
        stats.StoredByTier[1].Tier.ShouldBe(BlobTier.Archive);
        stats.StoredByTier[1].StoredSize.ShouldBe(60);
    }

    [Test]
    public async Task Handle_NoSnapshot_ReturnsZeroes()
    {
        var blobs = new FakeSeededBlobContainerService();
        await using var fixture = await RepositoryTestFixture.CreateWithEncryptionAsync(blobs, "acct-stats-empty", "ctr-stats-empty", IEncryptionService.PlaintextInstance);

        var handler = new StatisticsQueryHandler(fixture.Snapshot, fixture.Index, NullLogger<StatisticsQueryHandler>.Instance);
        var stats = await handler.Handle(new StatisticsQueryType(), CancellationToken.None);

        stats.Files.ShouldBe(0);
        stats.OriginalSize.ShouldBe(0);
        stats.DeduplicatedSize.ShouldBe(0);
        stats.UniqueChunks.ShouldBe(0);
        stats.StoredSize.ShouldBe(0);
    }
}
