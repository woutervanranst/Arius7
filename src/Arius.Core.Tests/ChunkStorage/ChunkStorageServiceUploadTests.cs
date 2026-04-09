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

    [Test]
    public async Task UploadLargeAsync_DeletesPartialExistingBlobAndRetries_WithNonSeekableInput()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var content = new byte[2048];
        Random.Shared.NextBytes(content);
        var blobName = BlobPaths.Chunk("retry-nonseek");

        await blobs.SeedLargeBlobAsync(blobName, content, BlobTier.Archive);
        blobs.ClearMetadata(blobName);
        blobs.ThrowAlreadyExistsOnOpenWrite(blobName, throwOnce: true);

        await using var nonSeekable = new NonSeekableReadStream(content);
        var result = await service.UploadLargeAsync(
            chunkHash: "retry-nonseek",
            content: nonSeekable,
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeFalse();
        blobs.DeletedBlobNames.ShouldContain(blobName);
        (await blobs.GetMetadataAsync(blobName)).Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeLarge);
    }

    [Test]
    public async Task UploadThinAsync_CreatesPointerBlobWithMetadata()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());

        var created = await service.UploadThinAsync(
            contentHash: "thin123",
            parentChunkHash: "tar123",
            originalSize: 512,
            compressedSize: 111,
            cancellationToken: CancellationToken.None);

        created.ShouldBeTrue();

        var blobName = BlobPaths.Chunk("thin123");
        var metadata = await blobs.GetMetadataAsync(blobName);
        metadata.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeThin);
        metadata.Metadata[BlobMetadataKeys.OriginalSize].ShouldBe("512");
        metadata.Metadata[BlobMetadataKeys.CompressedSize].ShouldBe("111");
        metadata.Tier.ShouldBe(BlobTier.Cool);

        await using var payload = await blobs.DownloadAsync(blobName);
        using var reader = new StreamReader(payload);
        (await reader.ReadToEndAsync()).ShouldBe("tar123");
    }

    [Test]
    public async Task UploadThinAsync_ReturnsFalse_WhenCommittedBlobAlreadyExists()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var blobName = BlobPaths.Chunk("thin456");

        blobs.SeedBlob(
            blobName,
            System.Text.Encoding.UTF8.GetBytes("tar456"),
            BlobTier.Cool,
            new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeThin,
                [BlobMetadataKeys.OriginalSize] = "123",
                [BlobMetadataKeys.CompressedSize] = "45",
            },
            ContentTypes.Thin);

        var created = await service.UploadThinAsync(
            contentHash: "thin456",
            parentChunkHash: "tar456",
            originalSize: 123,
            compressedSize: 45,
            cancellationToken: CancellationToken.None);

        created.ShouldBeFalse();
        blobs.DeletedBlobNames.ShouldNotContain(blobName);
    }

    [Test]
    public async Task UploadThinAsync_DeletesPartialExistingBlobAndRetries()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var blobName = BlobPaths.Chunk("thin789");

        blobs.SeedBlob(blobName, System.Text.Encoding.UTF8.GetBytes("tar-old"), BlobTier.Cool, metadata: new Dictionary<string, string>(), contentType: ContentTypes.Thin);

        var created = await service.UploadThinAsync(
            contentHash: "thin789",
            parentChunkHash: "tar789",
            originalSize: 789,
            compressedSize: 222,
            cancellationToken: CancellationToken.None);

        created.ShouldBeTrue();
        blobs.DeletedBlobNames.ShouldContain(blobName);

        var metadata = await blobs.GetMetadataAsync(blobName);
        metadata.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeThin);
    }
}

internal sealed class NonSeekableReadStream(byte[] content) : MemoryStream(content, writable: false)
{
    public override bool CanSeek => false;

    public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();

    public override long Position
    {
        get => base.Position;
        set => throw new NotSupportedException();
    }
}
