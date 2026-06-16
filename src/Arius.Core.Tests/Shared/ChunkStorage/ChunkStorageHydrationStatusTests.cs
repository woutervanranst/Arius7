using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Tests.Fakes;
using Arius.Tests.Shared.Compression;
using Arius.Tests.Shared.Encryption;

namespace Arius.Core.Tests.Shared.ChunkStorage;

public class ChunkStorageHydrationStatusTests
{
    private static readonly ChunkHash TestChunkHash = ChunkHash.Parse("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsAvailable_WhenPrimaryChunkIsHot()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.ChunkPath(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Hot };
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, TestCompression.Instance);

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
        blobs.RequestedBlobNames.ShouldBe([BlobPaths.ChunkPath(chunkHash)]);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsAvailable_WhenArchiveChunkHasCompletedRehydratedCopy()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.ChunkPath(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive };
        blobs.Metadata[BlobPaths.ChunkRehydratedPath(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Cool };
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, TestCompression.Instance);

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
        blobs.RequestedBlobNames.ShouldBe([BlobPaths.ChunkPath(chunkHash), BlobPaths.ChunkRehydratedPath(chunkHash)]);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsPending_WhenArchiveChunkIsRehydrating()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.ChunkPath(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = true };
        blobs.Metadata[BlobPaths.ChunkRehydratedPath(chunkHash)] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, TestCompression.Instance);

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.RehydrationPending);
    }

    [Test]
    public async Task GetHydrationStatusAsync_RehydratedCopyReady_TakesPrecedence()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.ChunkPath(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = true };
        blobs.Metadata[BlobPaths.ChunkRehydratedPath(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Hot };
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, TestCompression.Instance);

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
        blobs.RequestedBlobNames.ShouldBe([BlobPaths.ChunkPath(chunkHash), BlobPaths.ChunkRehydratedPath(chunkHash)]);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsPending_WhenRehydratedCopyExistsButStillArchive()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.ChunkPath(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = false };
        blobs.Metadata[BlobPaths.ChunkRehydratedPath(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive };
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, TestCompression.Instance);

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.RehydrationPending);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsNeedsRehydration_WhenArchiveChunkHasNoRehydratedCopy()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.ChunkPath(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = false };
        blobs.Metadata[BlobPaths.ChunkRehydratedPath(chunkHash)] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, TestCompression.Instance);

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.NeedsRehydration);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsUnknown_WhenPrimaryChunkDoesNotExist()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.ChunkPath(chunkHash)] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, TestCompression.Instance);

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Unknown);
        blobs.RequestedBlobNames.ShouldBe([BlobPaths.ChunkPath(chunkHash)]);
    }
}
