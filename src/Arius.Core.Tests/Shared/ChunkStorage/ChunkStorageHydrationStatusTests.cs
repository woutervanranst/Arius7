using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;

namespace Arius.Core.Tests.Shared.ChunkStorage;

public class ChunkStorageHydrationStatusTests
{
    private static ChunkHash Chunk(string value) => ChunkHash.Parse(value);

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsAvailable_WhenPrimaryChunkIsHot()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = Chunk("abc12300112233445566778899aabbccddeeff00112233445566778899aabb");
        blobs.Metadata[BlobPaths.Chunk(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Hot };
        var service = new ChunkStorageService(blobs, new Arius.Core.Shared.Encryption.PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
        blobs.RequestedBlobNames.ShouldBe([BlobPaths.Chunk(chunkHash)]);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsAvailable_WhenArchiveChunkHasCompletedRehydratedCopy()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = Chunk("abc12300112233445566778899aabbccddeeff00112233445566778899aabb");
        blobs.Metadata[BlobPaths.Chunk(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive };
        blobs.Metadata[BlobPaths.ChunkRehydrated(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Cool };
        var service = new ChunkStorageService(blobs, new Arius.Core.Shared.Encryption.PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
        blobs.RequestedBlobNames.ShouldBe([BlobPaths.Chunk(chunkHash), BlobPaths.ChunkRehydrated(chunkHash)]);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsPending_WhenArchiveChunkIsRehydrating()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = Chunk("abc12300112233445566778899aabbccddeeff00112233445566778899aabb");
        blobs.Metadata[BlobPaths.Chunk(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = true };
        blobs.Metadata[BlobPaths.ChunkRehydrated(chunkHash)] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, new Arius.Core.Shared.Encryption.PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.RehydrationPending);
    }

    [Test]
    public async Task GetHydrationStatusAsync_RehydratedCopyReady_TakesPrecedence()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = Chunk("abc12300112233445566778899aabbccddeeff00112233445566778899aabb");
        blobs.Metadata[BlobPaths.Chunk(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = true };
        blobs.Metadata[BlobPaths.ChunkRehydrated(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Hot };
        var service = new ChunkStorageService(blobs, new Arius.Core.Shared.Encryption.PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
        blobs.RequestedBlobNames.ShouldBe([BlobPaths.Chunk(chunkHash), BlobPaths.ChunkRehydrated(chunkHash)]);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsPending_WhenRehydratedCopyExistsButStillArchive()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = Chunk("abc12300112233445566778899aabbccddeeff00112233445566778899aabb");
        blobs.Metadata[BlobPaths.Chunk(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = false };
        blobs.Metadata[BlobPaths.ChunkRehydrated(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive };
        var service = new ChunkStorageService(blobs, new Arius.Core.Shared.Encryption.PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.RehydrationPending);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsNeedsRehydration_WhenArchiveChunkHasNoRehydratedCopy()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = Chunk("abc12300112233445566778899aabbccddeeff00112233445566778899aabb");
        blobs.Metadata[BlobPaths.Chunk(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = false };
        blobs.Metadata[BlobPaths.ChunkRehydrated(chunkHash)] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, new Arius.Core.Shared.Encryption.PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.NeedsRehydration);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsUnknown_WhenPrimaryChunkDoesNotExist()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = Chunk("abc12300112233445566778899aabbccddeeff00112233445566778899aabb");
        blobs.Metadata[BlobPaths.Chunk(chunkHash)] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, new Arius.Core.Shared.Encryption.PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Unknown);
        blobs.RequestedBlobNames.ShouldBe([BlobPaths.Chunk(chunkHash)]);
    }
}
