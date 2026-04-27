using Arius.Core.Features.ChunkHydrationStatusQuery;
using Arius.Core.Features.ListQuery;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Testing;

namespace Arius.Core.Tests.Features.ChunkHydrationStatusQuery;

public class ResolveFileHydrationStatusesHandlerTests
{
    private static readonly PlaintextPassthroughService s_encryption = new();

    [Test]
    [MatrixDataSource]
    public async Task Handle_ResolvesLargeAndTarBackedFileStatuses_Matrix(
        [Matrix(BlobMetadataKeys.TypeLarge, BlobMetadataKeys.TypeThin, BlobMetadataKeys.TypeTar)] string chunkType,
        [Matrix(HydrationBlobState.NonArchive, HydrationBlobState.Archive, HydrationBlobState.Rehydrating)] HydrationBlobState state)
    {
        var testCase = StatusCaseFor(state);
        var key = $"{chunkType}-{state}";
        var contentHash = ContentHashFor($"content-{key}");
        var largeChunkHash = chunkType == BlobMetadataKeys.TypeLarge ? ChunkHash.Parse(contentHash.ToString()) : ChunkHashFor($"large-{key}");
        var tarChunkHash = chunkType == BlobMetadataKeys.TypeTar ? ChunkHash.Parse(contentHash.ToString()) : ChunkHashFor($"tar-{key}");
        var resolvedChunkHash = chunkType switch
        {
            BlobMetadataKeys.TypeLarge => largeChunkHash,
            BlobMetadataKeys.TypeThin => tarChunkHash,
            BlobMetadataKeys.TypeTar => ChunkHash.Parse(contentHash.ToString()),
            _ => throw new ArgumentOutOfRangeException(nameof(chunkType), chunkType, null)
        };

        var blobs = new FakeMetadataOnlyBlobContainerService();
        testCase.ConfigureChunk(blobs, resolvedChunkHash, chunkType);

        using var index = new ChunkIndexService(blobs, s_encryption, $"acct-hydration-{key}", $"ctr-hydration-{key}", cacheBudgetBytes: 1024 * 1024);
        var entry = chunkType switch
        {
            BlobMetadataKeys.TypeLarge => new ShardEntry(contentHash, ChunkHash.Parse(contentHash.ToString()), 100, 25),
            BlobMetadataKeys.TypeThin => new ShardEntry(contentHash, tarChunkHash, 100, 25),
            BlobMetadataKeys.TypeTar => new ShardEntry(contentHash, ChunkHash.Parse(contentHash.ToString()), 100, 25),
            _ => throw new ArgumentOutOfRangeException(nameof(chunkType), chunkType, null)
        };
        index.AddEntry(entry);

        var handler = new ChunkHydrationStatusQueryHandler(
            index,
            new ChunkStorageService(blobs, s_encryption),
            new FakeLogger<ChunkHydrationStatusQueryHandler>());

        var files = new[]
        {
            new RepositoryFileEntry($"{chunkType}.bin", contentHash.ToString(), 100, null, null, true, false, null, null)
        };

        var results = new List<ChunkHydrationStatusResult>();
        await foreach (var result in handler.Handle(new Arius.Core.Features.ChunkHydrationStatusQuery.ChunkHydrationStatusQuery(files), CancellationToken.None))
            results.Add(result);

        results.Count.ShouldBe(1);
        results.ShouldContain(result => result.RelativePath == $"{chunkType}.bin" && result.Status == testCase.ExpectedStatus);
    }

    [Test]
    public async Task Handle_UsesBackingTarChunkStatus_ForThinFilesEvenWhenThinPointerBlobDiffers()
    {
        var thinContentHash = ContentHashFor("thin-content-special-case");
        var tarChunkHash    = ChunkHashFor("tar-chunk-special-case");
        var tarContentHash  = ContentHashFor("tar-content-special-case");

        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata[BlobPaths.Chunk(thinContentHash)] = new BlobMetadata
        {
            Exists = true,
            Tier = BlobTier.Hot,
            Metadata = new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeThin }
        };
        blobs.Metadata[BlobPaths.Chunk(tarChunkHash)] = new BlobMetadata
        {
            Exists = true,
            Tier = BlobTier.Archive,
            Metadata = new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeTar }
        };
        blobs.Metadata[BlobPaths.ChunkRehydrated(tarChunkHash)] = new BlobMetadata { Exists = false };

        using var index = new ChunkIndexService(blobs, s_encryption, "acct-hydration-thin-special", "ctr-hydration-thin-special", cacheBudgetBytes: 1024 * 1024);
        index.AddEntry(new ShardEntry(thinContentHash, tarChunkHash, 50, 10));
        index.AddEntry(new ShardEntry(tarContentHash, tarChunkHash, 75, 15));

        var handler = new ChunkHydrationStatusQueryHandler(
            index,
            new ChunkStorageService(blobs, s_encryption),
            new FakeLogger<ChunkHydrationStatusQueryHandler>());

        var files = new[]
        {
            new RepositoryFileEntry("thin.bin", thinContentHash.ToString(), 50, null, null, true, false, null, null),
            new RepositoryFileEntry("tar.bin", tarContentHash.ToString(), 75, null, null, true, false, null, null)
        };

        var results = new List<ChunkHydrationStatusResult>();
        await foreach (var result in handler.Handle(new Arius.Core.Features.ChunkHydrationStatusQuery.ChunkHydrationStatusQuery(files), CancellationToken.None))
            results.Add(result);

        results.Count.ShouldBe(2);
        results.ShouldContain(result => result.RelativePath == "thin.bin" && result.Status == ChunkHydrationStatus.NeedsRehydration);
        results.ShouldContain(result => result.RelativePath == "tar.bin" && result.Status == ChunkHydrationStatus.NeedsRehydration);
        blobs.RequestedBlobNames.ShouldNotContain(BlobPaths.Chunk(thinContentHash));
        blobs.RequestedBlobNames.ShouldContain(BlobPaths.Chunk(tarChunkHash));
    }

    [Test]
    public async Task Handle_InvalidContentHash_YieldsUnknownInsteadOfThrowing()
    {
        using var index = new ChunkIndexService(new FakeMetadataOnlyBlobContainerService(), s_encryption, "acct-hydration-invalid", "ctr-hydration-invalid", cacheBudgetBytes: 1024 * 1024);
        var handler = new ChunkHydrationStatusQueryHandler(
            index,
            new ChunkStorageService(new FakeMetadataOnlyBlobContainerService(), s_encryption),
            new FakeLogger<ChunkHydrationStatusQueryHandler>());

        var files = new[]
        {
            new RepositoryFileEntry("bad.bin", "not-a-hash", 100, null, null, true, false, null, null)
        };

        var results = new List<ChunkHydrationStatusResult>();
        await foreach (var result in handler.Handle(new Arius.Core.Features.ChunkHydrationStatusQuery.ChunkHydrationStatusQuery(files), CancellationToken.None))
            results.Add(result);

        results.Count.ShouldBe(1);
        results[0].RelativePath.ShouldBe("bad.bin");
        results[0].Status.ShouldBe(ChunkHydrationStatus.Unknown);
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
                    blobs.Metadata[BlobPaths.Chunk(chunkHash)] = new BlobMetadata
                    {
                        Exists = true,
                        Tier = BlobTier.Hot,
                        Metadata = new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = chunkType }
                    };
                }),
            HydrationBlobState.Archive => new HydrationStatusCase(
                "archive",
                ChunkHydrationStatus.NeedsRehydration,
                (blobs, chunkHash, chunkType) =>
                {
                    blobs.Metadata[BlobPaths.Chunk(chunkHash)] = new BlobMetadata
                    {
                        Exists = true,
                        Tier = BlobTier.Archive,
                        Metadata = new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = chunkType }
                    };
                    blobs.Metadata[BlobPaths.ChunkRehydrated(chunkHash)] = new BlobMetadata { Exists = false };
                }),
            HydrationBlobState.Rehydrating => new HydrationStatusCase(
                "rehydrating",
                ChunkHydrationStatus.RehydrationPending,
                (blobs, chunkHash, chunkType) =>
                {
                    blobs.Metadata[BlobPaths.Chunk(chunkHash)] = new BlobMetadata
                    {
                        Exists = true,
                        Tier = BlobTier.Archive,
                        IsRehydrating = true,
                        Metadata = new Dictionary<string, string> { [BlobMetadataKeys.AriusType] = chunkType }
                    };
                    blobs.Metadata[BlobPaths.ChunkRehydrated(chunkHash)] = new BlobMetadata { Exists = false };
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
    }

    private static ContentHash ContentHashFor(string label) => s_encryption.ComputeHash(System.Text.Encoding.UTF8.GetBytes(label));

    private static ChunkHash ChunkHashFor(string label) => ChunkHash.Parse(s_encryption.ComputeHash(System.Text.Encoding.UTF8.GetBytes(label)).ToString());

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
