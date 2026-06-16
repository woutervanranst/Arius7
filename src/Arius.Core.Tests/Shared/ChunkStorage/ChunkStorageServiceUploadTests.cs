using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Compression;
using Arius.Core.Tests.Shared.ChunkStorage.Fakes;
using Arius.Core.Tests.Shared.Streaming;
using Arius.Tests.Shared.Compression;
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

    [Test]
    public async Task UploadLargeAsync_StoresChunkAndReturnsStoredSize()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content);

        var result = await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
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
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
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
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
        var content = new byte[1536];
        Random.Shared.NextBytes(content);
        var tarChunkHash = ChunkHashOf(content);
        using var tarStream = new MemoryStream(content, writable: false);

        var result = await service.UploadTarAsync(
            chunkHash: tarChunkHash,
            content: tarStream,
            sourceSize: content.Length,
            tier: BlobTier.Cold,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeFalse();
        result.StoredSize.ShouldBeGreaterThan(0L);
        blobs.LastOpenWriteContentType.ShouldBe(ContentTypes.TarPlaintext);

        var metadata = await blobs.GetMetadataAsync(BlobPaths.ChunkPath(tarChunkHash));
        metadata.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeTar);
        metadata.Metadata[BlobMetadataKeys.ChunkSize].ShouldBe(result.StoredSize.ToString());
        metadata.Metadata.ContainsKey(BlobMetadataKeys.OriginalSize).ShouldBeFalse();

        // Distinct content so the large chunk lands at a different content-addressed path than the tar.
        var largeContent = new byte[1536];
        Random.Shared.NextBytes(largeContent);
        _ = await service.UploadLargeAsync(
            chunkHash: ChunkHashOf(largeContent),
            content: new MemoryStream(largeContent),
            sourceSize: largeContent.Length,
            tier: BlobTier.Cold,
            progress: null,
            cancellationToken: CancellationToken.None);

        blobs.OpenWriteContentTypes.ShouldBe([ContentTypes.TarPlaintext, ContentTypes.LargePlaintext]);
    }

    [Test]
    public async Task UploadLargeAsync_DeletesPartialExistingBlobAndRetries()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
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
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
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
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeTrue();
        result.OriginalSize.ShouldBe(content.Length);
    }

    [Test]
    public async Task UploadLargeAsync_OpenWriteConflict_FetchesMetadataAfterSuccessfulStreamUpload()
    {
        var blobs = new BlobAlreadyExistsOnSetMetadataOnceBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
        var content = new byte[2048];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content);

        var result = await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeFalse();
        blobs.MetadataRequests.ShouldContain(BlobPaths.ChunkPath(chunkHash));
    }

    [Test]
    public async Task UploadLargeAsync_RetryAfterMetadataConflict_ReportsSingleProgressSequence()
    {
        var blobs = new BlobAlreadyExistsOnSetMetadataOnceBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
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
            progress: progress,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeFalse();
        blobs.DeletedBlobNames.ShouldContain(blobName);
        reports.ShouldBe([512L, 1024L, 1536L, 2048L]);
    }

    [Test]
    public async Task UploadLargeAsync_Throws_WhenInputIsNotSeekable()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
        var content = new byte[2048];
        Random.Shared.NextBytes(content);

        await using var nonSeekable = new NonSeekableReadStream(content);

        await Should.ThrowAsync<InvalidOperationException>(() => service.UploadLargeAsync(
            chunkHash: NonSeekableInputChunkHash,
            content: nonSeekable,
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            progress: null,
            cancellationToken: CancellationToken.None));
    }

    [Test]
    public async Task UploadLargeAsync_FailsLoudly_AndDoesNotRecord_WhenStoredChunkDoesNotRoundTrip()
    {
        // Uploading content under a hash it does not produce simulates a frame that won't restore to its
        // chunk hash (e.g. an encoder bug). The inline round-trip verification must catch it: fail loudly
        // and leave no recorded blob, never silently store an unrecoverable chunk.
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var wrongHash = ChunkHash.Parse("0000000000000000000000000000000000000000000000000000000000000000");

        await Should.ThrowAsync<InvalidDataException>(() => service.UploadLargeAsync(
            chunkHash: wrongHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            progress: null,
            cancellationToken: CancellationToken.None));

        var metadata = await blobs.GetMetadataAsync(BlobPaths.ChunkPath(wrongHash));
        metadata.Exists.ShouldBeFalse();
    }

    [Test]
    public async Task UploadThinAsync_CreatesPointerBlobWithMetadata()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);

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
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
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
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
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
        // With a passphrase the service encrypts, so the blob must carry the encrypted content type
        // (the plaintext tests only exercise the unencrypted branch). The inline round-trip
        // verification still runs against the pre-encryption compressed bytes and must pass.
        var encryption = new PassphraseEncryptionService("correct horse battery staple");
        var blobs = new ContentTypeCapturingBlobContainerService();
        var service = new ChunkStorageService(blobs, encryption, TestCompression.Instance);
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content, encryption);

        var result = await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeFalse();
        blobs.LastOpenWriteContentType.ShouldBe(ContentTypes.LargeGcmEncrypted);
    }

    [Test]
    public async Task UploadLargeAsync_RewindsContent_WhenSourceIsNotAtStart()
    {
        // A source handed in mid-stream must be rewound to position 0 and read in full; the round-trip
        // verifier only reproduces the chunk hash if every byte from the start was compressed.
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), TestCompression.Instance);
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content);
        var source = new MemoryStream(content) { Position = 1234 };

        var result = await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: source,
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeFalse();
        result.OriginalSize.ShouldBe(content.Length);
    }

    [Test]
    public async Task UploadLargeAsync_SkipsRoundTripVerification_ForCodecThatDoesNotRequireIt()
    {
        // The zstd path verifies the stored chunk round-trips (see the wrong-hash test above); the legacy
        // gzip codec opts out (RequireRoundTripVerification == false), so ChunkStorageService skips the tee.
        // With verification skipped, an upload under a hash the content does NOT produce is recorded as-is
        // rather than failing loudly — proving the verification is gated on the codec's flag.
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), new GZipCompressionService());
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var wrongHash = ChunkHash.Parse("0000000000000000000000000000000000000000000000000000000000000000");

        var result = await service.UploadLargeAsync(
            chunkHash: wrongHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeFalse();
        (await blobs.GetMetadataAsync(BlobPaths.ChunkPath(wrongHash))).Exists.ShouldBeTrue();
    }

    [Test]
    public async Task UploadLargeAsync_GZipCodec_RoundTripsThroughUploadAndDownload()
    {
        // A full compress→encrypt→upload→download→decrypt→decompress cycle on the legacy gzip codec,
        // exercising the no-verifier upload path and proving ChunkStorageService both writes and reads gzip.
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService(), new GZipCompressionService());
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content);

        await service.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Cold,
            progress: null,
            cancellationToken: CancellationToken.None);

        await using var download = await service.DownloadAsync(chunkHash);
        var restored = new MemoryStream();
        await download.CopyToAsync(restored);
        restored.ToArray().ShouldBe(content);
    }

    [Test]
    public async Task UploadLargeAsync_NonVerifyingCodec_RoundTripsEncryptedMultiBlockContent()
    {
        // The riskiest combination this change introduces: the no-verifier upload path (verifier == null,
        // compression streams straight into the encryption chain with leaveOpen:true) under real AES-GCM, where
        // the encryption stream must be disposed exactly once so its auth tag is flushed before BytesWritten is
        // read. Use content larger than the 80 KiB CopyToAsync buffer so the frame spans multiple writes.
        var encryption = new PassphraseEncryptionService("correct horse battery staple");
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
        // Back-compat guarantee after extracting gzip decode into GZipCompressionService: a blob written by the
        // gzip era (gzip codec) must still decode when read through the production-registered zstd codec, whose
        // auto-detect now delegates legacy frames to GZipCompressionService — exercised end-to-end via the full
        // ChunkStorageService download stack.
        var blobs = new FakeInMemoryBlobContainerService();
        var encryption = new PlaintextPassthroughService();
        var legacyWriter = new ChunkStorageService(blobs, encryption, new GZipCompressionService());
        var productionReader = new ChunkStorageService(blobs, encryption, TestCompression.Instance);
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var chunkHash = ChunkHashOf(content, encryption);

        await legacyWriter.UploadLargeAsync(
            chunkHash: chunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Cold,
            progress: null,
            cancellationToken: CancellationToken.None);

        await using var download = await productionReader.DownloadAsync(chunkHash);
        var restored = new MemoryStream();
        await download.CopyToAsync(restored);
        restored.ToArray().ShouldBe(content);
    }

}
