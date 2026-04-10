using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Tests.Fakes;
using Arius.Core.Shared.Storage;
using Shouldly;

namespace Arius.Core.Tests.List;

public class ChunkStorageHydrationStatusTests
{
    [Test]
    public async Task GetHydrationStatusAsync_ReturnsAvailable_WhenPrimaryChunkIsHot()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Hot };
        var service = new ChunkStorageService(blobs, new Arius.Core.Shared.Encryption.PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync("abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
        blobs.RequestedBlobNames.ShouldBe(["chunks/abc"]);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsAvailable_WhenArchiveChunkHasCompletedRehydratedCopy()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive };
        blobs.Metadata["chunks-rehydrated/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Cool };
        var service = new ChunkStorageService(blobs, new Arius.Core.Shared.Encryption.PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync("abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
        blobs.RequestedBlobNames.ShouldBe(["chunks/abc", "chunks-rehydrated/abc"]);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsPending_WhenArchiveChunkIsRehydrating()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = true };
        blobs.Metadata["chunks-rehydrated/abc"] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, new Arius.Core.Shared.Encryption.PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync("abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.RehydrationPending);
    }

    [Test]
    public async Task GetHydrationStatusAsync_RehydratedCopyReady_TakesPrecedence()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = true };
        blobs.Metadata["chunks-rehydrated/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Hot };
        var service = new ChunkStorageService(blobs, new Arius.Core.Shared.Encryption.PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync("abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
        blobs.RequestedBlobNames.ShouldBe(["chunks/abc", "chunks-rehydrated/abc"]);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsPending_WhenRehydratedCopyExistsButStillArchive()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = false };
        blobs.Metadata["chunks-rehydrated/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive };
        var service = new ChunkStorageService(blobs, new Arius.Core.Shared.Encryption.PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync("abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.RehydrationPending);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsNeedsRehydration_WhenArchiveChunkHasNoRehydratedCopy()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = false };
        blobs.Metadata["chunks-rehydrated/abc"] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, new Arius.Core.Shared.Encryption.PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync("abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.NeedsRehydration);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsUnknown_WhenPrimaryChunkDoesNotExist()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        blobs.Metadata["chunks/abc"] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, new Arius.Core.Shared.Encryption.PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync("abc", CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Unknown);
        blobs.RequestedBlobNames.ShouldBe(["chunks/abc"]);
    }

}
