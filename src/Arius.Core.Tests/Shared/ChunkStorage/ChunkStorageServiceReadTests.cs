using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Tests.Fakes;
using Arius.Core.Tests.Shared.ChunkStorage.Fakes;
using Arius.Tests.Shared.Compression;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkStorage;

public class ChunkStorageServiceReadTests
{
    private static readonly ChunkHash TestChunkHash = ChunkHash.Parse("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsAvailable_WhenPrimaryChunkIsHot()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.ChunkPath(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Hot };
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsPending_WhenArchiveChunkIsRehydrating()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.ChunkPath(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = true };
        blobs.Metadata[BlobPaths.ChunkRehydratedPath(chunkHash)] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.RehydrationPending);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsAvailable_WhenArchiveChunkRehydratedCopyExists()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.ChunkPath(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive };
        blobs.Metadata[BlobPaths.ChunkRehydratedPath(chunkHash)] = new BlobMetadata { Exists = true };
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsMissing_WhenPrimaryChunkDoesNotExist()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.ChunkPath(chunkHash)] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Unknown);
    }

    [Test]
    public async Task DownloadAsync_UsesRehydratedBlobWhenAvailable_AndReturnsPlaintext()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var encryption = new PlaintextPassthroughService();
        var service = new ChunkStorageService(blobs, encryption, TestCompression.Instance);
        var content = "hello from rehydrated"u8.ToArray();
        var chunkHash = TestChunkHash;

        await blobs.SeedLargeBlobAsync(BlobPaths.ChunkRehydratedPath(chunkHash), content, BlobTier.Cold);
        await blobs.SeedLargeBlobAsync(BlobPaths.ChunkPath(chunkHash), "old"u8.ToArray(), BlobTier.Archive);

        await using var stream = await service.DownloadAsync(chunkHash, cancellationToken: CancellationToken.None);
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).ShouldBe("hello from rehydrated");
    }

    [Test]
    public async Task DownloadAsync_CanBeDisposedSynchronously()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
        var chunkHash = TestChunkHash;
        await blobs.SeedLargeBlobAsync(BlobPaths.ChunkPath(chunkHash), "hello"u8.ToArray(), BlobTier.Hot);

        var stream = await service.DownloadAsync(chunkHash, cancellationToken: CancellationToken.None);
        stream.Dispose();
    }

    [Test]
    public async Task StartRehydrationAsync_CopiesPrimaryChunkToRehydratedPrefix()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
        var chunkHash = TestChunkHash;
        await blobs.SeedLargeBlobAsync(BlobPaths.ChunkPath(chunkHash), "rehydrate"u8.ToArray(), BlobTier.Archive);

        await service.StartRehydrationAsync(chunkHash, RehydratePriority.High, CancellationToken.None);

        var rehydrated = await blobs.GetMetadataAsync(BlobPaths.ChunkRehydratedPath(chunkHash));
        rehydrated.Exists.ShouldBeTrue();
        rehydrated.Tier.ShouldBe(BlobTier.Cold);
    }

    [Test]
    public async Task PlanRehydratedCleanupAsync_ReportsAndDeletesPlannedBlobs()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
        var firstChunkHash = FakeChunkHash('a');
        var secondChunkHash = FakeChunkHash('b');
        await blobs.SeedLargeBlobAsync(BlobPaths.ChunkRehydratedPath(firstChunkHash), "aaa"u8.ToArray(), BlobTier.Cold);
        await blobs.SeedLargeBlobAsync(BlobPaths.ChunkRehydratedPath(secondChunkHash), "bbbb"u8.ToArray(), BlobTier.Cold);

        await using var plan = await service.PlanRehydratedCleanupAsync(CancellationToken.None);

        plan.ChunkCount.ShouldBe(2);
        plan.TotalBytes.ShouldBeGreaterThan(0L);

        var result = await plan.ExecuteAsync(CancellationToken.None);

        result.DeletedChunkCount.ShouldBe(2);
        (await blobs.GetMetadataAsync(BlobPaths.ChunkRehydratedPath(firstChunkHash))).Exists.ShouldBeFalse();
        (await blobs.GetMetadataAsync(BlobPaths.ChunkRehydratedPath(secondChunkHash))).Exists.ShouldBeFalse();
    }

    [Test]
    public async Task PlanRehydratedCleanupAsync_DeletesBlobsInParallel()
    {
        var blobs = new BlockingDeleteBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);

        await using var plan = await service.PlanRehydratedCleanupAsync(CancellationToken.None);
        var executeTask = plan.ExecuteAsync(CancellationToken.None);

        await blobs.WaitForConcurrentDeletesAsync();
        blobs.ReleaseDeletes();

        var result = await executeTask;

        result.DeletedChunkCount.ShouldBe(2);
    }
}
