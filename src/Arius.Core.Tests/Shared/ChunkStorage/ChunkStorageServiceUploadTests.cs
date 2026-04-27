using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;
using Arius.Core.Tests.Shared.ChunkStorage.Fakes;

namespace Arius.Core.Tests.Shared.ChunkStorage;

public class ChunkStorageServiceUploadTests
{
    private static ChunkHash Chunk(string value) => ChunkHash.Parse(value);

    private static ContentHash Content(string value) => ContentHash.Parse(value);

    [Test]
    public async Task UploadLargeAsync_StoresChunkAndReturnsStoredSize()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var content = new byte[4096];
        Random.Shared.NextBytes(content);

        var result = await service.UploadLargeAsync(
            chunkHash: Chunk("abc12300112233445566778899aabbccddeeff00112233445566778899aabb"),
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.ChunkHash.ShouldBe(Chunk("abc12300112233445566778899aabbccddeeff00112233445566778899aabb"));
        result.StoredSize.ShouldBeGreaterThan(0L);
        result.AlreadyExisted.ShouldBeFalse();

        var metadata = await blobs.GetMetadataAsync(BlobPaths.Chunk(Chunk("abc12300112233445566778899aabbccddeeff00112233445566778899aabb")));
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

        await blobs.SeedTarBlobAsync(BlobPaths.Chunk(Chunk("tar12300112233445566778899aabbccddeeff00112233445566778899aabb")), [content], BlobTier.Cold);
        blobs.ThrowAlreadyExistsOnOpenWrite(BlobPaths.Chunk(Chunk("tar12300112233445566778899aabbccddeeff00112233445566778899aabb")));

        var result = await service.UploadTarAsync(
            chunkHash: Chunk("tar12300112233445566778899aabbccddeeff00112233445566778899aabb"),
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Cold,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeTrue();
        result.StoredSize.ShouldBeGreaterThan(0L);
        blobs.DeletedBlobNames.ShouldNotContain(BlobPaths.Chunk(Chunk("tar12300112233445566778899aabbccddeeff00112233445566778899aabb")));
    }

    [Test]
    public async Task UploadLargeAsync_DeletesPartialExistingBlobAndRetries()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var content = new byte[2048];
        Random.Shared.NextBytes(content);
        var blobName = BlobPaths.Chunk(Chunk("abcabc00112233445566778899aabbccddeeff00112233445566778899aabb"));

        await blobs.SeedLargeBlobAsync(blobName, content, BlobTier.Archive);
        blobs.ClearMetadata(blobName);
        blobs.ThrowAlreadyExistsOnOpenWrite(blobName, throwOnce: true);

        var result = await service.UploadLargeAsync(
            chunkHash: Chunk("abcabc00112233445566778899aabbccddeeff00112233445566778899aabb"),
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
    public async Task UploadLargeAsync_Throws_WhenInputIsNotSeekable()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var content = new byte[2048];
        Random.Shared.NextBytes(content);

        await using var nonSeekable = new NonSeekableReadStream(content);

        await Should.ThrowAsync<InvalidOperationException>(() => service.UploadLargeAsync(
            chunkHash: Chunk("feeded00112233445566778899aabbccddeeff00112233445566778899aabb"),
            content: nonSeekable,
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            progress: null,
            cancellationToken: CancellationToken.None));
    }

    [Test]
    public async Task UploadThinAsync_CreatesPointerBlobWithMetadata()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());

        var created = await service.UploadThinAsync(
            contentHash: Content("11112300112233445566778899aabbccddeeff00112233445566778899aabb"),
            parentChunkHash: Chunk("22212300112233445566778899aabbccddeeff00112233445566778899aabb"),
            originalSize: 512,
            compressedSize: 111,
            cancellationToken: CancellationToken.None);

        created.ShouldBeTrue();

        var blobName = BlobPaths.Chunk(Content("11112300112233445566778899aabbccddeeff00112233445566778899aabb"));
        var metadata = await blobs.GetMetadataAsync(blobName);
        metadata.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeThin);
        metadata.Metadata[BlobMetadataKeys.OriginalSize].ShouldBe("512");
        metadata.Metadata[BlobMetadataKeys.CompressedSize].ShouldBe("111");
        metadata.Tier.ShouldBe(BlobTier.Cool);

        await using var payload = await blobs.DownloadAsync(blobName);
        using var reader = new StreamReader(payload);
        (await reader.ReadToEndAsync()).ShouldBe("22212300112233445566778899aabbccddeeff00112233445566778899aabb");
    }

    [Test]
    public async Task UploadThinAsync_ReturnsFalse_WhenCommittedBlobAlreadyExists()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var blobName = BlobPaths.Chunk(Content("11145600112233445566778899aabbccddeeff00112233445566778899aabb"));

        blobs.SeedBlob(
            blobName,
            System.Text.Encoding.UTF8.GetBytes("22245600112233445566778899aabbccddeeff00112233445566778899aabb"),
            BlobTier.Cool,
            new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeThin,
                [BlobMetadataKeys.OriginalSize] = "123",
                [BlobMetadataKeys.CompressedSize] = "45",
            },
            ContentTypes.Thin);

        var created = await service.UploadThinAsync(
            contentHash: Content("11145600112233445566778899aabbccddeeff00112233445566778899aabb"),
            parentChunkHash: Chunk("22245600112233445566778899aabbccddeeff00112233445566778899aabb"),
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
        var blobName = BlobPaths.Chunk(Content("11178900112233445566778899aabbccddeeff00112233445566778899aabb"));

        blobs.SeedBlob(blobName, System.Text.Encoding.UTF8.GetBytes("22200000112233445566778899aabbccddeeff00112233445566778899aabb"), BlobTier.Cool, metadata: new Dictionary<string, string>(), contentType: ContentTypes.Thin);

        var created = await service.UploadThinAsync(
            contentHash: Content("11178900112233445566778899aabbccddeeff00112233445566778899aabb"),
            parentChunkHash: Chunk("22278900112233445566778899aabbccddeeff00112233445566778899aabb"),
            originalSize: 789,
            compressedSize: 222,
            cancellationToken: CancellationToken.None);

        created.ShouldBeTrue();
        blobs.DeletedBlobNames.ShouldContain(blobName);

        var metadata = await blobs.GetMetadataAsync(blobName);
        metadata.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeThin);
    }
}
