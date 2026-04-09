using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;
using Shouldly;

namespace Arius.Core.Tests.ChunkStorage;

public class ChunkStorageServiceUploadTests
{
    [Test]
    public async Task UploadLargeAsync_StoresChunkAndReturnsStoredSize()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var content = new byte[4096];
        Random.Shared.NextBytes(content);

        var result = await service.UploadLargeAsync(
            chunkHash: "abc123",
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.ChunkHash.ShouldBe("abc123");
        result.StoredSize.ShouldBeGreaterThan(0L);
        result.AlreadyExisted.ShouldBeFalse();

        var metadata = await blobs.GetMetadataAsync(BlobPaths.Chunk("abc123"));
        metadata.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeLarge);
        metadata.Metadata[BlobMetadataKeys.OriginalSize].ShouldBe(content.Length.ToString());
        metadata.Metadata[BlobMetadataKeys.ChunkSize].ShouldBe(result.StoredSize.ToString());
        metadata.Tier.ShouldBe(BlobTier.Archive);
    }

    [Test]
    public async Task UploadTarAsync_ReusesCompletedExistingBlob()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var content = new byte[1024];
        Random.Shared.NextBytes(content);

        await blobs.SeedTarBlobAsync(BlobPaths.Chunk("tar123"), [content], BlobTier.Cold);
        blobs.ThrowAlreadyExistsOnOpenWrite(BlobPaths.Chunk("tar123"));

        var result = await service.UploadTarAsync(
            chunkHash: "tar123",
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Cold,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeTrue();
        result.StoredSize.ShouldBeGreaterThan(0L);
        blobs.DeletedBlobNames.ShouldNotContain(BlobPaths.Chunk("tar123"));
    }

    [Test]
    public async Task UploadLargeAsync_DeletesPartialExistingBlobAndRetries()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var content = new byte[2048];
        Random.Shared.NextBytes(content);
        var blobName = BlobPaths.Chunk("retry123");

        await blobs.SeedLargeBlobAsync(blobName, content, BlobTier.Archive);
        blobs.ClearMetadata(blobName);
        blobs.ThrowAlreadyExistsOnOpenWrite(blobName, throwOnce: true);

        var result = await service.UploadLargeAsync(
            chunkHash: "retry123",
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeFalse();
        blobs.DeletedBlobNames.ShouldContain(blobName);

        var metadata = await blobs.GetMetadataAsync(blobName);
        metadata.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeLarge);
    }
}
