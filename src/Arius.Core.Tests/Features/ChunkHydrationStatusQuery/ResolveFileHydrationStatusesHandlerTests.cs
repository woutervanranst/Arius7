using Arius.Core.Features.ChunkHydrationStatusQuery;
using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Tests.Fakes;
using Arius.Core.Tests.Shared.Snapshot.Fakes;
using Arius.Tests.Shared.Compression;
using Microsoft.Extensions.Logging.Testing;

namespace Arius.Core.Tests.Features.ChunkHydrationStatusQuery;

public class ResolveFileHydrationStatusesHandlerTests
{

    [Test]
    [MatrixDataSource]
    public async Task Handle_ResolvesLargeAndTarBackedFileStatuses_Matrix(
        [Matrix(BlobMetadataKeys.TypeLarge, BlobMetadataKeys.TypeThin, BlobMetadataKeys.TypeTar)] string chunkType,
        [Matrix(HydrationBlobState.NonArchive, HydrationBlobState.Archive, HydrationBlobState.Rehydrating)] HydrationBlobState state)
    {
        var testCase       = StatusCaseFor(state);
        var key            = $"{chunkType}-{state}";
        var contentHash    = FakeContentHash('a');
        var largeChunkHash = chunkType == BlobMetadataKeys.TypeLarge ? ChunkHash.Parse(contentHash) : FakeChunkHash('b');
        var tarChunkHash   = chunkType == BlobMetadataKeys.TypeTar ? ChunkHash.Parse(contentHash) : FakeChunkHash('c');
        var resolvedChunkHash = chunkType switch
        {
            BlobMetadataKeys.TypeLarge => largeChunkHash,
            BlobMetadataKeys.TypeThin  => tarChunkHash,
            BlobMetadataKeys.TypeTar   => ChunkHash.Parse(contentHash),
            _                          => throw new ArgumentOutOfRangeException(nameof(chunkType), chunkType, null)
        };

        var blobs = new FakeMetadataOnlyBlobContainerService();
        testCase.ConfigureChunk(blobs, resolvedChunkHash, chunkType);

        var snapshot = new FakeSnapshotService();
        using var index = new ChunkIndexService(blobs, TestEncryption.Instance, TestCompression.Instance, snapshot, $"acct-hydration-{key}", $"ctr-hydration-{key}");
        var entry = chunkType switch
        {
            BlobMetadataKeys.TypeLarge => new ShardEntry(contentHash, ChunkHash.Parse(contentHash), 100, 25, BlobTier.Cool),
            BlobMetadataKeys.TypeThin  => new ShardEntry(contentHash, tarChunkHash,                 100, 25, BlobTier.Cool),
            BlobMetadataKeys.TypeTar   => new ShardEntry(contentHash, ChunkHash.Parse(contentHash), 100, 25, BlobTier.Cool),
            _                          => throw new ArgumentOutOfRangeException(nameof(chunkType), chunkType, null)
        };
        index.AddEntry(entry);

        var handler = new ChunkHydrationStatusQueryHandler(
            index,
            new ChunkStorageService(blobs, TestEncryption.Instance, TestCompression.Instance),
            new FakeLogger<ChunkHydrationStatusQueryHandler>());

        var files = new[]
        {
            new RepositoryFileEntry(RelativePath.Parse($"{chunkType}.bin"), RepositoryEntryState.Repository, contentHash, 100, null, null)
        };

        var results = new List<ChunkHydrationStatusResult>();
        await foreach (var result in handler.Handle(new Arius.Core.Features.ChunkHydrationStatusQuery.ChunkHydrationStatusQuery(files), CancellationToken.None))
            results.Add(result);

        results.Count.ShouldBe(1);
        results.ShouldContain(result => result.RelativePath == RelativePath.Parse($"{chunkType}.bin") && result.Status == testCase.ExpectedStatus);
    }

    [Test]
    public async Task Handle_UsesBackingTarChunkStatus_ForThinFilesEvenWhenThinPointerBlobDiffers()
    {
        var thinContentHash = FakeContentHash('d');
        var tarChunkHash    = FakeChunkHash('e');
        var tarContentHash  = FakeContentHash('f');

        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata[BlobPaths.ThinChunkPath(thinContentHash)] = new BlobMetadata
        {
            Exists   = true,
            Tier     = BlobTier.Hot,
            Metadata = new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeThin }
        };
        blobs.Metadata[BlobPaths.ChunkPath(tarChunkHash)] = new BlobMetadata
        {
            Exists   = true,
            Tier     = BlobTier.Archive,
            Metadata = new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeTar }
        };
        blobs.Metadata[BlobPaths.ChunkRehydratedPath(tarChunkHash)] = new BlobMetadata { Exists = false };

        var snapshot = new FakeSnapshotService();
        using var index = new ChunkIndexService(blobs, TestEncryption.Instance, TestCompression.Instance, snapshot, "acct-hydration-thin-special", "ctr-hydration-thin-special");
        index.AddEntry(new ShardEntry(thinContentHash, tarChunkHash, 50, 10, BlobTier.Cool));
        index.AddEntry(new ShardEntry(tarContentHash, tarChunkHash, 75, 15, BlobTier.Cool));

        var handler = new ChunkHydrationStatusQueryHandler(
            index,
            new ChunkStorageService(blobs, TestEncryption.Instance, TestCompression.Instance),
            new FakeLogger<ChunkHydrationStatusQueryHandler>());

        var files = new[]
        {
            new RepositoryFileEntry(RelativePath.Parse("thin.bin"), RepositoryEntryState.Repository, thinContentHash, 50, null, null),
            new RepositoryFileEntry(RelativePath.Parse("tar.bin"), RepositoryEntryState.Repository, tarContentHash, 75, null, null)
        };

        var results = new List<ChunkHydrationStatusResult>();
        await foreach (var result in handler.Handle(new Arius.Core.Features.ChunkHydrationStatusQuery.ChunkHydrationStatusQuery(files), CancellationToken.None))
            results.Add(result);

        results.Count.ShouldBe(2);
        results.ShouldContain(result => result.RelativePath == RelativePath.Parse("thin.bin") && result.Status == ChunkHydrationStatus.NeedsRehydration);
        results.ShouldContain(result => result.RelativePath == RelativePath.Parse("tar.bin") && result.Status == ChunkHydrationStatus.NeedsRehydration);
        blobs.RequestedBlobNames.ShouldNotContain(BlobPaths.ThinChunkPath(thinContentHash));
        blobs.RequestedBlobNames.ShouldContain(BlobPaths.ChunkPath(tarChunkHash));
    }

    private static HydrationStatusCase StatusCaseFor(HydrationBlobState state)
    {
        return state switch
        {
            HydrationBlobState.NonArchive => new HydrationStatusCase(
                "non-archive",
                ChunkHydrationStatus.Available,
                (blobs, chunkHash, chunkType) =>
                {
                    blobs.Metadata[BlobPaths.ChunkPath(chunkHash)] = new BlobMetadata
                    {
                        Exists   = true,
                        Tier     = BlobTier.Hot,
                        Metadata = new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = chunkType }
                    };
                }),
            HydrationBlobState.Archive => new HydrationStatusCase(
                "archive",
                ChunkHydrationStatus.NeedsRehydration,
                (blobs, chunkHash, chunkType) =>
                {
                    blobs.Metadata[BlobPaths.ChunkPath(chunkHash)] = new BlobMetadata
                    {
                        Exists   = true,
                        Tier     = BlobTier.Archive,
                        Metadata = new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = chunkType }
                    };
                    blobs.Metadata[BlobPaths.ChunkRehydratedPath(chunkHash)] = new BlobMetadata { Exists = false };
                }),
            HydrationBlobState.Rehydrating => new HydrationStatusCase(
                "rehydrating",
                ChunkHydrationStatus.RehydrationPending,
                (blobs, chunkHash, chunkType) =>
                {
                    blobs.Metadata[BlobPaths.ChunkPath(chunkHash)] = new BlobMetadata
                    {
                        Exists        = true,
                        Tier          = BlobTier.Archive,
                        IsRehydrating = true,
                        Metadata      = new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = chunkType }
                    };
                    blobs.Metadata[BlobPaths.ChunkRehydratedPath(chunkHash)] = new BlobMetadata { Exists = false };
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
    }

    private sealed record HydrationStatusCase(
        string Name,
        ChunkHydrationStatus ExpectedStatus,
        Action<FakeMetadataOnlyBlobContainerService, ChunkHash, string> ConfigureChunk);

    public enum HydrationBlobState
    {
        NonArchive,
        Archive,
        Rehydrating
    }
}
