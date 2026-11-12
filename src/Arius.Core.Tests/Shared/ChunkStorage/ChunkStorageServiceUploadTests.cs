using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Compression;
using Arius.Core.Tests.Shared.ChunkStorage.Fakes;
using Arius.Core.Tests.Shared.Streaming;
using Arius.Tests.Shared;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkStorage;

public class ChunkStorageServiceUploadTests
{
    private static readonly ChunkHash   ExistingTarChunkHash        = ChunkHash.Parse("fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210");
    private static readonly ChunkHash   ExistingLargeChunkHash      = ChunkHash.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly ChunkHash   NonSeekableInputChunkHash   = ChunkHash.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly ContentHash ThinContentHash             = ContentHash.Parse("1111111111111111111111111111111111111111111111111111111111111111");
    private static readonly ChunkHash   ThinParentChunkHash         = ChunkHash.Parse("2222222222222222222222222222222222222222222222222222222222222222");
    private static readonly ContentHash ExistingThinContentHash     = ContentHash.Parse("3333333333333333333333333333333333333333333333333333333333333333");
    private static readonly ChunkHash   ExistingThinParentChunkHash = ChunkHash.Parse("4444444444444444444444444444444444444444444444444444444444444444");
    private static readonly ContentHash RetryThinContentHash        = ContentHash.Parse("5555555555555555555555555555555555555555555555555555555555555555");
    private static readonly ChunkHash   RetryThinParentChunkHash    = ChunkHash.Parse("6666666666666666666666666666666666666666666666666666666666666666");

    private const long NoTierCeiling = 0;

    [Test]
    [Arguments(false, 0)]
    [Arguments(true, 0)]
    [Arguments(false, 256 * 1024)]
    [Arguments(true, 256 * 1024)]
    public async Task UploadLargeAsync_WithoutDecodingOrRehashing_StoresCompleteRestorableChunk(bool encrypted, int size)
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var encryption = encrypted ? IEncryptionService.EncryptedInstance : IEncryptionService.PlaintextInstance;
        var service = new ChunkStorageService(blobs, new EncryptOnlyEncryptionService(encryption), new EncodeOnlyCompressionService());
        var content = new byte[size];
        new Random(220).NextBytes(content);
        var chunkHash = ChunkHash.Parse(encryption.ComputeHash(content));
        using var source = new MemoryStream(content, writable: false);

        var result = await service.UploadLargeAsync(chunkHash, source, size, BlobTier.Hot, NoTierCeiling);

        source.CanRead.ShouldBeTrue();
        var metadata = await blobs.GetMetadataAsync(BlobPaths.ChunkPath(chunkHash));
        result.StoredSize.ShouldBe(metadata.ContentLength!.Value);
        metadata.Metadata[BlobMetadataKeys.ChunkSize].ShouldBe(result.StoredSize.ToString());
        var stored = await blobs.DownloadAsync(BlobPaths.ChunkPath(chunkHash));
        await using var decrypted = encryption.WrapForDecryption(stored.Stream);
        await using var decoded = new ZstdCompressionService().WrapForDecompression(decrypted);
        using var restored = new MemoryStream();
        await decoded.CopyToAsync(restored);
        restored.ToArray().ShouldBe(content);
    }

    [Test]
    public async Task UploadLargeAsync_StoresChunkAndReturnsStoredSize()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content);

        var result = await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            smallFileThreshold: NoTierCeiling,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.ChunkHash.ShouldBe(chunkHash);
        result.StoredSize.ShouldBeGreaterThan(0L);
        result.AlreadyExisted.ShouldBeFalse();
        result.OriginalSize.ShouldBe(content.Length);

        var metadata = await blobs.GetMetadataAsync(BlobPaths.ChunkPath(chunkHash));
        metadata.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeLarge);
        metadata.Metadata[BlobMetadataKeys.OriginalSize].ShouldBe(content.Length.ToString());
        metadata.Metadata[BlobMetadataKeys.ChunkSize].ShouldBe(result.StoredSize.ToString());
        metadata.Tier.ShouldBe(BlobTier.Archive);
    }

    [Test]
    public async Task UploadTarAsync_ReusesCompletedExistingBlob()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = new byte[1024];
        Random.Shared.NextBytes(content);
        using var tarStream = new MemoryStream(content, writable: false);

        await blobs.SeedTarBlobAsync(BlobPaths.ChunkPath(ExistingTarChunkHash), [content], BlobTier.Cold);
        blobs.ThrowAlreadyExistsOnOpenWrite(BlobPaths.ChunkPath(ExistingTarChunkHash));

        var result = await service.UploadTarAsync(
            chunkHash: ExistingTarChunkHash,
            content: tarStream,
            sourceSize: content.Length,
            tier: BlobTier.Cold,
            smallFileThreshold: NoTierCeiling,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeTrue();
        result.StoredSize.ShouldBeGreaterThan(0L);
        result.OriginalSize.ShouldBeNull();
        blobs.DeletedBlobNames.ShouldNotContain(BlobPaths.ChunkPath(ExistingTarChunkHash));
    }

    [Test]
    public async Task UploadTarAsync_StoresTarMetadataWithoutOriginalSize_AndUsesPlaintextTarContentType()
    {
        var blobs = new ContentTypeCapturingBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = new byte[1536];
        Random.Shared.NextBytes(content);
        var tarChunkHash = ChunkHashOf(content);
        using var tarStream = new MemoryStream(content, writable: false);

        var result = await service.UploadTarAsync(
            chunkHash: tarChunkHash,
            content: tarStream,
            sourceSize: content.Length,
            tier: BlobTier.Cold,
            smallFileThreshold: NoTierCeiling,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeFalse();
        result.StoredSize.ShouldBeGreaterThan(0L);
        blobs.LastOpenWriteContentType.ShouldBe(ContentTypes.TarPlaintext);

        var metadata = await blobs.GetMetadataAsync(BlobPaths.ChunkPath(tarChunkHash));
        metadata.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeTar);
        metadata.Metadata[BlobMetadataKeys.ChunkSize].ShouldBe(result.StoredSize.ToString());
        metadata.Metadata.ContainsKey(BlobMetadataKeys.OriginalSize).ShouldBeFalse();

        // Use a different hash to avoid reusing the tar chunk.
        var largeContent = new byte[1536];
        Random.Shared.NextBytes(largeContent);
        _ = await service.UploadLargeAsync(
            chunkHash: ChunkHashOf(largeContent),
            content: new MemoryStream(largeContent),
            sourceSize: largeContent.Length,
            tier: BlobTier.Cold,
            smallFileThreshold: NoTierCeiling,
            progress: null,
            cancellationToken: CancellationToken.None);

        blobs.OpenWriteContentTypes.ShouldBe([ContentTypes.TarPlaintext, ContentTypes.LargePlaintext]);
    }

    [Test]
    public async Task UploadLargeAsync_DeletesPartialExistingBlobAndRetries()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = new byte[2048];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content);
        var blobName = BlobPaths.ChunkPath(chunkHash);

        await blobs.SeedLargeBlobAsync(blobName, content, BlobTier.Archive);
        blobs.ClearMetadata(blobName);
        blobs.ThrowAlreadyExistsOnOpenWrite(blobName, throwOnce: true);

        var result = await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            smallFileThreshold: NoTierCeiling,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeFalse();
        blobs.DeletedBlobNames.ShouldContain(blobName);

        var metadata = await blobs.GetMetadataAsync(blobName);
        metadata.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeLarge);
    }

    [Test]
    public async Task UploadLargeAsync_ReturnsOriginalSizeFromExistingCommittedBlob()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = new byte[2048];
        Random.Shared.NextBytes(content);
        var blobName = BlobPaths.ChunkPath(ExistingLargeChunkHash);

        await blobs.SeedLargeBlobAsync(blobName, content, BlobTier.Archive);
        blobs.ThrowAlreadyExistsOnOpenWrite(blobName);

        var result = await service.UploadLargeAsync(
            chunkHash: ExistingLargeChunkHash,
            content: new MemoryStream(content),
            sourceSize: 999,
            tier: BlobTier.Archive,
            smallFileThreshold: NoTierCeiling,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeTrue();
        result.OriginalSize.ShouldBe(content.Length);
    }

    [Test]
    public async Task UploadLargeAsync_OpenWriteConflict_FetchesMetadataAfterSuccessfulStreamUpload()
    {
        var blobs = new BlobAlreadyExistsOnSetMetadataOnceBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = new byte[2048];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content);

        var result = await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            smallFileThreshold: NoTierCeiling,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeFalse();
        blobs.MetadataRequests.ShouldContain(BlobPaths.ChunkPath(chunkHash));
    }

    [Test]
    public async Task UploadLargeAsync_RetryAfterMetadataConflict_ReportsSingleProgressSequence()
    {
        var blobs = new BlobAlreadyExistsOnSetMetadataOnceBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = new byte[2048];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content);
        var reports = new List<long>();
        var progress = new SyncProgress<long>(value => reports.Add(value));
        var blobName = BlobPaths.ChunkPath(chunkHash);
        await using var chunkedContent = new ChunkedReadMemoryStream(content, maxChunkSize: 512);

        var result = await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: chunkedContent,
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            smallFileThreshold: NoTierCeiling,
            progress: progress,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeFalse();
        blobs.DeletedBlobNames.ShouldContain(blobName);

        reports.ShouldNotBeEmpty();
        reports.ShouldBeInOrder();
        reports.Distinct().Count().ShouldBe(reports.Count);
        reports[^1].ShouldBe((long)content.Length);
    }

    [Test]
    public async Task UploadLargeAsync_Throws_WhenInputIsNotSeekable()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = new byte[2048];
        Random.Shared.NextBytes(content);

        await using var nonSeekable = new NonSeekableReadStream(content);

        await Should.ThrowAsync<InvalidOperationException>(() => service.UploadLargeAsync(
            chunkHash: NonSeekableInputChunkHash,
            content: nonSeekable,
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            smallFileThreshold: NoTierCeiling,
            progress: null,
            cancellationToken: CancellationToken.None));
    }

    [Test]
    public async Task UploadThinAsync_CreatesPointerBlobWithMetadata()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);

        var created = await service.UploadThinAsync(
            contentHash: ThinContentHash,
            parentChunkHash: ThinParentChunkHash,
            originalSize: 512,
            chunkSize: 111,
            cancellationToken: CancellationToken.None);

        created.ShouldBeTrue();

        var blobName = BlobPaths.ThinChunkPath(ThinContentHash);
        var metadata = await blobs.GetMetadataAsync(BlobPaths.ThinChunkPath(ThinContentHash));
        metadata.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeThin);
        metadata.Metadata[BlobMetadataKeys.ParentChunkHash].ShouldBe(ThinParentChunkHash.ToString());
        metadata.Metadata[BlobMetadataKeys.OriginalSize].ShouldBe("512");
        metadata.Metadata[BlobMetadataKeys.ChunkSize].ShouldBe("111");
        metadata.Tier.ShouldBe(BlobTier.Cool);

        var download = await blobs.DownloadAsync(BlobPaths.ThinChunkPath(ThinContentHash));
        await using var payload = download.Stream;
        payload.Length.ShouldBe(0);
    }

    [Test]
    public async Task UploadThinAsync_ReturnsFalse_WhenCommittedBlobAlreadyExists()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var blobName = BlobPaths.ThinChunkPath(ExistingThinContentHash);

        blobs.SeedBlob(
            blobName,
            Array.Empty<byte>(),
            BlobTier.Cool,
            new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeThin,
            },
            ContentTypes.Thin);

        var created = await service.UploadThinAsync(
            contentHash: ExistingThinContentHash,
            parentChunkHash: ExistingThinParentChunkHash,
            originalSize: 123,
            chunkSize: 45,
            cancellationToken: CancellationToken.None);

        created.ShouldBeFalse();
        blobs.DeletedBlobNames.ShouldNotContain(blobName);
    }

    [Test]
    public async Task UploadThinAsync_DeletesPartialExistingBlobAndRetries()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var blobName = BlobPaths.ThinChunkPath(RetryThinContentHash);

        blobs.SeedBlob(blobName, Array.Empty<byte>(), BlobTier.Cool, metadata: new Dictionary<string, string>(), contentType: ContentTypes.Thin);

        var created = await service.UploadThinAsync(
            contentHash: RetryThinContentHash,
            parentChunkHash: RetryThinParentChunkHash,
            originalSize: 789,
            chunkSize: 222,
            cancellationToken: CancellationToken.None);

        created.ShouldBeTrue();
        blobs.DeletedBlobNames.ShouldContain(blobName);

        var metadata = await blobs.GetMetadataAsync(BlobPaths.ThinChunkPath(RetryThinContentHash));
        metadata.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeThin);
        metadata.Metadata[BlobMetadataKeys.ParentChunkHash].ShouldBe(RetryThinParentChunkHash.ToString());
    }

    [Test]
    public async Task UploadLargeAsync_EncryptedRepository_TagsBlobWithGcmEncryptedContentType()
    {
        var encryption = IEncryptionService.EncryptedInstance;
        var blobs = new ContentTypeCapturingBlobContainerService();
        var service = new ChunkStorageService(blobs, encryption, ICompressionService.ZtdInstance);
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content, encryption);

        var result = await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            smallFileThreshold: NoTierCeiling,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeFalse();
        blobs.LastOpenWriteContentType.ShouldBe(ContentTypes.LargeGcmEncrypted);
    }

    [Test]
    public async Task UploadLargeAsync_RewindsContent_WhenSourceIsNotAtStart()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content);
        var source = new MemoryStream(content) { Position = 1234 };

        var result = await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: source,
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            smallFileThreshold: NoTierCeiling,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeFalse();
        result.OriginalSize.ShouldBe(content.Length);
    }

    [Test]
    public async Task UploadLargeAsync_GZipCodec_RoundTripsThroughUploadAndDownload()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, new GZipCompressionService());
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content);

        await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Cold,
            smallFileThreshold: NoTierCeiling,
            progress: null,
            cancellationToken: CancellationToken.None);

        await using var download = await service.DownloadAsync(chunkHash);
        var restored = new MemoryStream();
        await download.CopyToAsync(restored);
        restored.ToArray().ShouldBe(content);
    }

    [Test]
    public async Task UploadLargeAsync_GZipCodec_RoundTripsEncryptedMultiBlockContent()
    {
        // Exercise authentication-tag finalization across multiple writes.
        var encryption = IEncryptionService.EncryptedInstance;
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, encryption, new GZipCompressionService());
        var content = new byte[200_000];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content, encryption);

        var result = await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Cold,
            smallFileThreshold: NoTierCeiling,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.StoredSize.ShouldBeGreaterThan(0L);

        await using var download = await service.DownloadAsync(chunkHash);
        var restored = new MemoryStream();
        await download.CopyToAsync(restored);
        restored.ToArray().ShouldBe(content);
    }

    [Test]
    public async Task DownloadAsync_ReadsLegacyGZipBlob_ThroughProductionZstdCodec()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var legacyWriter = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, new GZipCompressionService());
        var productionReader = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content, IEncryptionService.PlaintextInstance);

        await legacyWriter.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Cold,
            smallFileThreshold: NoTierCeiling,
            progress: null,
            cancellationToken: CancellationToken.None);

        await using var download = await productionReader.DownloadAsync(chunkHash);
        var restored = new MemoryStream();
        await download.CopyToAsync(restored);
        restored.ToArray().ShouldBe(content);
    }


    [Test]
    public async Task UploadLargeAsync_KeepsChunkOnline_WhenArchiveRequestedAndStoredSizeIsWithinTheThreshold()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = CompressibleContent(2 * 1024 * 1024);
        var chunkHash = ChunkHashOf(content);

        var result = await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            smallFileThreshold: 1024 * 1024,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.StoredSize.ShouldBeLessThanOrEqualTo(1024 * 1024);
        result.ActualTier.ShouldBe(BlobTier.Cold);
        (await blobs.GetMetadataAsync(BlobPaths.ChunkPath(chunkHash))).Tier.ShouldBe(BlobTier.Cold);
    }

    [Test]
    public async Task UploadTarAsync_KeepsTarOnline_WhenArchiveRequestedAndStoredSizeIsWithinTheThreshold()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = CompressibleContent(2 * 1024 * 1024);
        var tarChunkHash = ChunkHashOf(content);

        var result = await service.UploadTarAsync(
            chunkHash: tarChunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            smallFileThreshold: 1024 * 1024,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.ActualTier.ShouldBe(BlobTier.Cold);
        (await blobs.GetMetadataAsync(BlobPaths.ChunkPath(tarChunkHash))).Tier.ShouldBe(BlobTier.Cold);
    }

    [Test]
    public async Task UploadLargeAsync_KeepsArchiveTier_WhenStoredSizeExceedsTheThreshold()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content);

        var result = await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            smallFileThreshold: 1024 * 1024,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.StoredSize.ShouldBeGreaterThan(1024L * 1024);
        result.ActualTier.ShouldBe(BlobTier.Archive);
        (await blobs.GetMetadataAsync(BlobPaths.ChunkPath(chunkHash))).Tier.ShouldBe(BlobTier.Archive);
    }

    [Test]
    public async Task UploadLargeAsync_KeepsRequestedOnlineTier_WhenStoredSizeIsWithinTheThreshold()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = CompressibleContent(4096);
        var chunkHash = ChunkHashOf(content);

        var result = await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Cool,
            smallFileThreshold: 1024 * 1024,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.ActualTier.ShouldBe(BlobTier.Cool);
        (await blobs.GetMetadataAsync(BlobPaths.ChunkPath(chunkHash))).Tier.ShouldBe(BlobTier.Cool);
    }

    // Reusing a chunk must not trigger a paid rehydration.
    [Test]
    public async Task UploadLargeAsync_ReportsTierOfExistingBlob_WhenBlobAlreadyExisted()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, IEncryptionService.PlaintextInstance, ICompressionService.ZtdInstance);
        var content = new byte[2048];
        Random.Shared.NextBytes(content);
        var blobName = BlobPaths.ChunkPath(ExistingLargeChunkHash);

        await blobs.SeedLargeBlobAsync(blobName, content, BlobTier.Archive);
        blobs.ThrowAlreadyExistsOnOpenWrite(blobName);

        var result = await service.UploadLargeAsync(
            chunkHash: ExistingLargeChunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Cool,
            smallFileThreshold: 1024 * 1024,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeTrue();
        result.ActualTier.ShouldBe(BlobTier.Archive);
        (await blobs.GetMetadataAsync(blobName)).Tier.ShouldBe(BlobTier.Archive);
    }

    private static byte[] CompressibleContent(int sizeBytes)
    {
        var content = new byte[sizeBytes];
        Array.Fill(content, (byte)7);
        return content;
    }
}
