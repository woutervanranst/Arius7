using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;
using Shouldly;

namespace Arius.Core.Tests.ChunkStorage;

public class ChunkStorageServiceReadTests
{
    [Test]
    public async Task GetHydrationStatusAsync_ReturnsAvailable_WhenPrimaryChunkIsHot()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata[BlobPaths.Chunk("abc")] = new BlobMetadata { Exists = true, Tier = BlobTier.Hot };
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync("abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsPending_WhenArchiveChunkIsRehydrating()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata[BlobPaths.Chunk("abc")] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = true };
        blobs.Metadata[BlobPaths.ChunkRehydrated("abc")] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync("abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.RehydrationPending);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsAvailable_WhenArchiveChunkRehydratedCopyExists()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata[BlobPaths.Chunk("abc")] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive };
        blobs.Metadata[BlobPaths.ChunkRehydrated("abc")] = new BlobMetadata { Exists = true };
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync("abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsMissing_WhenPrimaryChunkDoesNotExist()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata[BlobPaths.Chunk("abc")] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync("abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Unknown);
    }

    [Test]
    public async Task DownloadAsync_UsesRehydratedBlobWhenAvailable_AndReturnsPlaintext()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var encryption = new PlaintextPassthroughService();
        var service = new ChunkStorageService(blobs, encryption);
        var content = System.Text.Encoding.UTF8.GetBytes("hello from rehydrated");

        await blobs.SeedLargeBlobAsync(BlobPaths.ChunkRehydrated("abc"), content, BlobTier.Cold);
        await blobs.SeedLargeBlobAsync(BlobPaths.Chunk("abc"), System.Text.Encoding.UTF8.GetBytes("old"), BlobTier.Archive);

        await using var stream = await service.DownloadAsync("abc", cancellationToken: CancellationToken.None);
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).ShouldBe("hello from rehydrated");
    }

    [Test]
    public async Task DownloadAsync_CanBeDisposedSynchronously()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        await blobs.SeedLargeBlobAsync(BlobPaths.Chunk("abc"), System.Text.Encoding.UTF8.GetBytes("hello"), BlobTier.Hot);

        var stream = await service.DownloadAsync("abc", cancellationToken: CancellationToken.None);
        stream.Dispose();
    }

    [Test]
    public async Task StartRehydrationAsync_CopiesPrimaryChunkToRehydratedPrefix()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        await blobs.SeedLargeBlobAsync(BlobPaths.Chunk("abc"), System.Text.Encoding.UTF8.GetBytes("rehydrate"), BlobTier.Archive);

        await service.StartRehydrationAsync("abc", RehydratePriority.High, CancellationToken.None);

        var rehydrated = await blobs.GetMetadataAsync(BlobPaths.ChunkRehydrated("abc"));
        rehydrated.Exists.ShouldBeTrue();
        rehydrated.Tier.ShouldBe(BlobTier.Cold);
    }

    [Test]
    public async Task PlanRehydratedCleanupAsync_ReportsAndDeletesPlannedBlobs()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        await blobs.SeedLargeBlobAsync(BlobPaths.ChunkRehydrated("a"), System.Text.Encoding.UTF8.GetBytes("aaa"), BlobTier.Cold);
        await blobs.SeedLargeBlobAsync(BlobPaths.ChunkRehydrated("b"), System.Text.Encoding.UTF8.GetBytes("bbbb"), BlobTier.Cold);

        await using var plan = await service.PlanRehydratedCleanupAsync(CancellationToken.None);

        plan.ChunkCount.ShouldBe(2);
        plan.TotalBytes.ShouldBeGreaterThan(0L);

        var result = await plan.ExecuteAsync(CancellationToken.None);

        result.DeletedChunkCount.ShouldBe(2);
        result.FreedBytes.ShouldBe(plan.TotalBytes);
        (await blobs.GetMetadataAsync(BlobPaths.ChunkRehydrated("a"))).Exists.ShouldBeFalse();
        (await blobs.GetMetadataAsync(BlobPaths.ChunkRehydrated("b"))).Exists.ShouldBeFalse();
    }

    [Test]
    public async Task PlanRehydratedCleanupAsync_DeletesBlobsInParallel()
    {
        var blobs = new BlockingDeleteBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());

        await using var plan = await service.PlanRehydratedCleanupAsync(CancellationToken.None);
        var executeTask = plan.ExecuteAsync(CancellationToken.None);

        await blobs.WaitForConcurrentDeletesAsync();
        blobs.ReleaseDeletes();

        var result = await executeTask;

        result.DeletedChunkCount.ShouldBe(2);
        result.FreedBytes.ShouldBe(7);
    }

    private sealed class BlockingDeleteBlobContainerService : IBlobContainerService
    {
        private readonly TaskCompletionSource _sawConcurrentDeletes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDeletes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeDeletes;

        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IAsyncEnumerable<string> ListAsync(string prefix = "", CancellationToken cancellationToken = default)
            => AsyncEnumerable();

        public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default)
            => Task.FromResult(blobName switch
            {
                "chunks-rehydrated/a" => new BlobMetadata { Exists = true, ContentLength = 3 },
                "chunks-rehydrated/b" => new BlobMetadata { Exists = true, ContentLength = 4 },
                _ => new BlobMetadata { Exists = false }
            });

        public async Task DeleteAsync(string blobName, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _activeDeletes) >= 2)
                _sawConcurrentDeletes.TrySetResult();

            await _releaseDeletes.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref _activeDeletes);
        }

        public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task WaitForConcurrentDeletesAsync() => _sawConcurrentDeletes.Task;

        public void ReleaseDeletes() => _releaseDeletes.TrySetResult();

        private async IAsyncEnumerable<string> AsyncEnumerable()
        {
            yield return BlobPaths.ChunkRehydrated("a");
            yield return BlobPaths.ChunkRehydrated("b");
            await Task.CompletedTask;
        }
    }
}
