using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Shared.ChunkStorage.Fakes;
using Arius.Core.Tests.Shared.Streaming;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkStorage;

public class ChunkStorageServiceUploadTests
{
    private static readonly ChunkHash LargeChunkHash = ChunkHash.Parse("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
    private static readonly ChunkHash TarChunkHash = ChunkHash.Parse("fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210");
    private static readonly ChunkHash RetryChunkHash = ChunkHash.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly ChunkHash NonSeekableChunkHash = ChunkHash.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly ContentHash ThinContentHash = ContentHash.Parse("1111111111111111111111111111111111111111111111111111111111111111");
    private static readonly ChunkHash ThinParentChunkHash = ChunkHash.Parse("2222222222222222222222222222222222222222222222222222222222222222");
    private static readonly ContentHash ExistingThinContentHash = ContentHash.Parse("3333333333333333333333333333333333333333333333333333333333333333");
    private static readonly ChunkHash ExistingThinParentChunkHash = ChunkHash.Parse("4444444444444444444444444444444444444444444444444444444444444444");
    private static readonly ContentHash RetryThinContentHash = ContentHash.Parse("5555555555555555555555555555555555555555555555555555555555555555");
    private static readonly ChunkHash RetryThinParentChunkHash = ChunkHash.Parse("6666666666666666666666666666666666666666666666666666666666666666");

    [Test]
    public async Task UploadLargeAsync_StoresChunkAndReturnsStoredSize()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var content = new byte[4096];
        Random.Shared.NextBytes(content);

        var result = await service.UploadLargeAsync(
            chunkHash: LargeChunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Archive,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.ChunkHash.ShouldBe(LargeChunkHash);
        result.StoredSize.ShouldBeGreaterThan(0L);
        result.AlreadyExisted.ShouldBeFalse();

        var metadata = await blobs.GetMetadataAsync(BlobPaths.Chunk(LargeChunkHash));
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

        await blobs.SeedTarBlobAsync(BlobPaths.Chunk(TarChunkHash), [content], BlobTier.Cold);
        blobs.ThrowAlreadyExistsOnOpenWrite(BlobPaths.Chunk(TarChunkHash));

        var result = await service.UploadTarAsync(
            chunkHash: TarChunkHash,
            content: new MemoryStream(content),
            sourceSize: content.Length,
            tier: BlobTier.Cold,
            progress: null,
            cancellationToken: CancellationToken.None);

        result.AlreadyExisted.ShouldBeTrue();
        result.StoredSize.ShouldBeGreaterThan(0L);
        blobs.DeletedBlobNames.ShouldNotContain(BlobPaths.Chunk(TarChunkHash));
    }

    [Test]
    public async Task UploadLargeAsync_DeletesPartialExistingBlobAndRetries()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var content = new byte[2048];
        Random.Shared.NextBytes(content);
        var blobName = BlobPaths.Chunk(RetryChunkHash);

        await blobs.SeedLargeBlobAsync(blobName, content, BlobTier.Archive);
        blobs.ClearMetadata(blobName);
        blobs.ThrowAlreadyExistsOnOpenWrite(blobName, throwOnce: true);

        var result = await service.UploadLargeAsync(
            chunkHash: RetryChunkHash,
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
    public async Task UploadLargeAsync_RetryAfterMetadataConflict_ReportsSingleProgressSequence()
    {
        var blobs = new BlobAlreadyExistsOnSetMetadataOnceBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var content = new byte[2048];
        Random.Shared.NextBytes(content);
        var reports = new List<long>();
        var progress = new SyncProgress<long>(value => reports.Add(value));
        var blobName = BlobPaths.Chunk(RetryChunkHash);
        await using var chunkedContent = new ChunkedReadMemoryStream(content, maxChunkSize: 512);

        var result = await service.UploadLargeAsync(
            chunkHash: RetryChunkHash,
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
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var content = new byte[2048];
        Random.Shared.NextBytes(content);

        await using var nonSeekable = new NonSeekableReadStream(content);

        await Should.ThrowAsync<InvalidOperationException>(() => service.UploadLargeAsync(
            chunkHash: NonSeekableChunkHash,
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
            contentHash: ThinContentHash,
            parentChunkHash: ThinParentChunkHash,
            originalSize: 512,
            compressedSize: 111,
            cancellationToken: CancellationToken.None);

        created.ShouldBeTrue();

        var blobName = BlobPaths.ThinChunk(ThinContentHash);
        var metadata = await blobs.GetMetadataAsync(blobName);
        metadata.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeThin);
        metadata.Metadata[BlobMetadataKeys.OriginalSize].ShouldBe("512");
        metadata.Metadata[BlobMetadataKeys.CompressedSize].ShouldBe("111");
        metadata.Tier.ShouldBe(BlobTier.Cool);

        await using var payload = await blobs.DownloadAsync(blobName);
        using var reader = new StreamReader(payload);
        (await reader.ReadToEndAsync()).ShouldBe(ThinParentChunkHash.ToString());
    }

    [Test]
    public async Task UploadThinAsync_ReturnsFalse_WhenCommittedBlobAlreadyExists()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var blobName = BlobPaths.ThinChunk(ExistingThinContentHash);

        blobs.SeedBlob(
            blobName,
            System.Text.Encoding.UTF8.GetBytes(ExistingThinParentChunkHash.ToString()),
            BlobTier.Cool,
            new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeThin,
                [BlobMetadataKeys.OriginalSize] = "123",
                [BlobMetadataKeys.CompressedSize] = "45",
            },
            ContentTypes.Thin);

        var created = await service.UploadThinAsync(
            contentHash: ExistingThinContentHash,
            parentChunkHash: ExistingThinParentChunkHash,
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
        var blobName = BlobPaths.ThinChunk(RetryThinContentHash);

        blobs.SeedBlob(blobName, System.Text.Encoding.UTF8.GetBytes(RetryThinParentChunkHash.ToString()), BlobTier.Cool, metadata: new Dictionary<string, string>(), contentType: ContentTypes.Thin);

        var created = await service.UploadThinAsync(
            contentHash: RetryThinContentHash,
            parentChunkHash: RetryThinParentChunkHash,
            originalSize: 789,
            compressedSize: 222,
            cancellationToken: CancellationToken.None);

        created.ShouldBeTrue();
        blobs.DeletedBlobNames.ShouldContain(blobName);

        var metadata = await blobs.GetMetadataAsync(blobName);
        metadata.Metadata[BlobMetadataKeys.AriusType].ShouldBe(BlobMetadataKeys.TypeThin);
    }

    private sealed class BlobAlreadyExistsOnSetMetadataOnceBlobContainerService : IBlobContainerService
    {
        private readonly FakeInMemoryBlobContainerService _inner = new();
        private int _remainingFailures = 1;

        public ICollection<string> DeletedBlobNames => _inner.DeletedBlobNames;

        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) =>
            _inner.CreateContainerIfNotExistsAsync(cancellationToken);

        public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) =>
            _inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);

        public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
            _inner.OpenWriteAsync(blobName, contentType, cancellationToken);

        public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) =>
            _inner.DownloadAsync(blobName, cancellationToken);

        public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default) =>
            _inner.GetMetadataAsync(blobName, cancellationToken);

        public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
            _inner.ListAsync(prefix, cancellationToken);

        public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _remainingFailures, 0) == 1)
                throw new BlobAlreadyExistsException(blobName);

            return _inner.SetMetadataAsync(blobName, metadata, cancellationToken);
        }

        public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) =>
            _inner.SetTierAsync(blobName, tier, cancellationToken);

        public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) =>
            _inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

        public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(blobName, cancellationToken);
    }

    private sealed class ChunkedReadMemoryStream(byte[] buffer, int maxChunkSize) : MemoryStream(buffer, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, maxChunkSize));

        public override int Read(Span<byte> buffer) =>
            base.Read(buffer[..Math.Min(buffer.Length, maxChunkSize)]);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            base.ReadAsync(buffer, offset, Math.Min(count, maxChunkSize), cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maxChunkSize)], cancellationToken);
    }
}
