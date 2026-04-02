using Arius.Core.Features.Hydration;
using Arius.Core.Features.List;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Arius.Core.Tests.List;

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
        var contentHash = HashFor($"content-{key}");
        var largeChunkHash = chunkType == BlobMetadataKeys.TypeLarge ? contentHash : HashFor($"large-{key}");
        var tarChunkHash = chunkType == BlobMetadataKeys.TypeTar ? contentHash : HashFor($"tar-{key}");
        var resolvedChunkHash = chunkType switch
        {
            BlobMetadataKeys.TypeLarge => largeChunkHash,
            BlobMetadataKeys.TypeThin => tarChunkHash,
            BlobMetadataKeys.TypeTar => contentHash,
            _ => throw new ArgumentOutOfRangeException(nameof(chunkType), chunkType, null)
        };

        var blobs = new FakeBlobContainerService();
        testCase.ConfigureChunk(blobs, resolvedChunkHash, chunkType);

        using var index = new ChunkIndexService(blobs, s_encryption, $"acct-hydration-{key}", $"ctr-hydration-{key}", cacheBudgetBytes: 1024 * 1024);
        var entry = chunkType switch
        {
            BlobMetadataKeys.TypeLarge => new ShardEntry(contentHash, contentHash, 100, 25),
            BlobMetadataKeys.TypeThin => new ShardEntry(contentHash, tarChunkHash, 100, 25),
            BlobMetadataKeys.TypeTar => new ShardEntry(contentHash, contentHash, 100, 25),
            _ => throw new ArgumentOutOfRangeException(nameof(chunkType), chunkType, null)
        };
        index.RecordEntry(entry);

        var handler = new ResolveFileHydrationStatusesHandler(
            blobs,
            index,
            NullLogger<ResolveFileHydrationStatusesHandler>.Instance);

        var files = new[]
        {
            new RepositoryFileEntry($"{chunkType}.bin", contentHash, 100, null, null, true, false, null, null)
        };

        var results = new List<FileHydrationStatusResult>();
        await foreach (var result in handler.Handle(new ResolveFileHydrationStatusesCommand(files), CancellationToken.None))
            results.Add(result);

        results.Count.ShouldBe(1);
        results.ShouldContain(result => result.RelativePath == $"{chunkType}.bin" && result.Status == testCase.ExpectedStatus);
    }

    [Test]
    public async Task Handle_UsesBackingTarChunkStatus_ForThinFilesEvenWhenThinPointerBlobDiffers()
    {
        var thinContentHash = HashFor("thin-content-special-case");
        var tarChunkHash    = HashFor("tar-chunk-special-case");
        var tarContentHash  = HashFor("tar-content-special-case");

        var blobs = new FakeBlobContainerService();
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
        index.RecordEntry(new ShardEntry(thinContentHash, tarChunkHash, 50, 10));
        index.RecordEntry(new ShardEntry(tarContentHash, tarChunkHash, 75, 15));

        var handler = new ResolveFileHydrationStatusesHandler(
            blobs,
            index,
            NullLogger<ResolveFileHydrationStatusesHandler>.Instance);

        var files = new[]
        {
            new RepositoryFileEntry("thin.bin", thinContentHash, 50, null, null, true, false, null, null),
            new RepositoryFileEntry("tar.bin", tarContentHash, 75, null, null, true, false, null, null)
        };

        var results = new List<FileHydrationStatusResult>();
        await foreach (var result in handler.Handle(new ResolveFileHydrationStatusesCommand(files), CancellationToken.None))
            results.Add(result);

        results.Count.ShouldBe(2);
        results.ShouldContain(result => result.RelativePath == "thin.bin" && result.Status == FileHydrationStatus.NeedsRehydration);
        results.ShouldContain(result => result.RelativePath == "tar.bin" && result.Status == FileHydrationStatus.NeedsRehydration);
        blobs.RequestedBlobNames.ShouldNotContain(BlobPaths.Chunk(thinContentHash));
        blobs.RequestedBlobNames.ShouldContain(BlobPaths.Chunk(tarChunkHash));
    }

    private static HydrationStatusCase StatusCaseFor(HydrationBlobState state)
    {
        return state switch
        {
            HydrationBlobState.NonArchive => new HydrationStatusCase(
                "non-archive",
                FileHydrationStatus.Available,
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
                FileHydrationStatus.NeedsRehydration,
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
                FileHydrationStatus.RehydrationPending,
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

    private static string HashFor(string label) => Convert.ToHexString(s_encryption.ComputeHash(System.Text.Encoding.UTF8.GetBytes(label))).ToLowerInvariant();

    private sealed record HydrationStatusCase(
        string Name,
        FileHydrationStatus ExpectedStatus,
        Action<FakeBlobContainerService, string, string> ConfigureChunk);

    public enum HydrationBlobState
    {
        NonArchive,
        Archive,
        Rehydrating
    }

    private sealed class FakeBlobContainerService : IBlobContainerService
    {
        public Dictionary<string, BlobMetadata> Metadata { get; } = new(StringComparer.Ordinal);
        public List<string> RequestedBlobNames { get; } = [];

        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default)
        {
            RequestedBlobNames.Add(blobName);
            return Task.FromResult(Metadata.TryGetValue(blobName, out var metadata) ? metadata : new BlobMetadata { Exists = false });
        }

        public async IAsyncEnumerable<string> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
    }
}
