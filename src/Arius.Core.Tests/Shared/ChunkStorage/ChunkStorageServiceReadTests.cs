using System.IO.Compression;
using Arius.Core.Shared.ChunkStorage;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Arius.Core.Tests.Fakes;
using Arius.Core.Tests.Shared.ChunkStorage.Fakes;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkStorage;

public class ChunkStorageServiceReadTests
{
    private static readonly ChunkHash TestChunkHash = ChunkHash.Parse("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

    [Test]
    public async Task DownloadAsync_WithProgress_DisposesDownloadPipelineExactlyOnce()
    {
        var blobs = new DisposalTrackingBlobContainerService(Compress("hello"u8.ToArray()));
        var encryption = new DisposalTrackingEncryptionService();
        var service = new ChunkStorageService(blobs, encryption);

        var stream = await service.DownloadAsync(TestChunkHash, new Progress<long>(_ => { }), CancellationToken.None);

        stream.Dispose();

        encryption.DecryptStream.ShouldNotBeNull();
        encryption.DecryptStream.DisposeCount.ShouldBe(1);
        blobs.DownloadStream.DisposeCount.ShouldBe(1);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsAvailable_WhenPrimaryChunkIsHot()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.Chunk(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Hot };
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsPending_WhenArchiveChunkIsRehydrating()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.Chunk(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive, IsRehydrating = true };
        blobs.Metadata[BlobPaths.ChunkRehydrated(chunkHash)] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.RehydrationPending);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsAvailable_WhenArchiveChunkRehydratedCopyExists()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.Chunk(chunkHash)] = new BlobMetadata { Exists = true, Tier = BlobTier.Archive };
        blobs.Metadata[BlobPaths.ChunkRehydrated(chunkHash)] = new BlobMetadata { Exists = true };
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Available);
    }

    [Test]
    public async Task GetHydrationStatusAsync_ReturnsMissing_WhenPrimaryChunkDoesNotExist()
    {
        var blobs = new FakeMetadataOnlyBlobContainerService();
        var chunkHash = TestChunkHash;
        blobs.Metadata[BlobPaths.Chunk(chunkHash)] = new BlobMetadata { Exists = false };
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());

        var status = await service.GetHydrationStatusAsync(chunkHash, CancellationToken.None);

        status.ShouldBe(ChunkHydrationStatus.Unknown);
    }

    [Test]
    public async Task DownloadAsync_UsesRehydratedBlobWhenAvailable_AndReturnsPlaintext()
    {
        var blobs      = new FakeInMemoryBlobContainerService();
        var encryption = new PlaintextPassthroughService();
        var service    = new ChunkStorageService(blobs, encryption);
        var content    = "hello from rehydrated"u8.ToArray();
        var chunkHash  = TestChunkHash;

        await blobs.SeedLargeBlobAsync(BlobPaths.ChunkRehydrated(chunkHash), content, BlobTier.Cold);
        await blobs.SeedLargeBlobAsync(BlobPaths.Chunk(chunkHash),           "old"u8.ToArray(), BlobTier.Archive);

        await using var stream = await service.DownloadAsync(chunkHash, cancellationToken: CancellationToken.None);
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).ShouldBe("hello from rehydrated");
    }

    [Test]
    public async Task DownloadAsync_CanBeDisposedSynchronously()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var chunkHash = TestChunkHash;
        await blobs.SeedLargeBlobAsync(BlobPaths.Chunk(chunkHash), "hello"u8.ToArray(), BlobTier.Hot);

        var stream = await service.DownloadAsync(chunkHash, cancellationToken: CancellationToken.None);
        stream.Dispose();
    }

    [Test]
    public async Task DownloadAsync_WithProgress_DisposesDownloadPipelineExactlyOnceAsynchronously()
    {
        var blobs = new DisposalTrackingBlobContainerService(Compress("hello"u8.ToArray()));
        var encryption = new DisposalTrackingEncryptionService();
        var service = new ChunkStorageService(blobs, encryption);

        var stream = await service.DownloadAsync(TestChunkHash, new Progress<long>(_ => { }), CancellationToken.None);

        await stream.DisposeAsync();
        await stream.DisposeAsync();

        encryption.DecryptStream.ShouldNotBeNull();
        encryption.DecryptStream.AsyncDisposeCount.ShouldBe(1);
        encryption.DecryptStream.SyncDisposeCount.ShouldBe(0);
        blobs.DownloadStream.AsyncDisposeCount.ShouldBe(0);
        blobs.DownloadStream.SyncDisposeCount.ShouldBe(1);
    }

    [Test]
    public async Task StartRehydrationAsync_CopiesPrimaryChunkToRehydratedPrefix()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var chunkHash = TestChunkHash;
        await blobs.SeedLargeBlobAsync(BlobPaths.Chunk(chunkHash), "rehydrate"u8.ToArray(), BlobTier.Archive);

        await service.StartRehydrationAsync(chunkHash, RehydratePriority.High, CancellationToken.None);

        var rehydrated = await blobs.GetMetadataAsync(BlobPaths.ChunkRehydrated(chunkHash));
        rehydrated.Exists.ShouldBeTrue();
        rehydrated.Tier.ShouldBe(BlobTier.Cold);
    }

    [Test]
    public async Task PlanRehydratedCleanupAsync_ReportsAndDeletesPlannedBlobs()
    {
        var blobs = new FakeInMemoryBlobContainerService();
        var service = new ChunkStorageService(blobs, new PlaintextPassthroughService());
        var firstChunkHash = FakeChunkHash('a');
        var secondChunkHash = FakeChunkHash('b');
        await blobs.SeedLargeBlobAsync(BlobPaths.ChunkRehydrated(firstChunkHash),  "aaa"u8.ToArray(),  BlobTier.Cold);
        await blobs.SeedLargeBlobAsync(BlobPaths.ChunkRehydrated(secondChunkHash), "bbbb"u8.ToArray(), BlobTier.Cold);

        await using var plan = await service.PlanRehydratedCleanupAsync(CancellationToken.None);

        plan.ChunkCount.ShouldBe(2);
        plan.TotalBytes.ShouldBeGreaterThan(0L);

        var result = await plan.ExecuteAsync(CancellationToken.None);

        result.DeletedChunkCount.ShouldBe(2);
        result.FreedBytes.ShouldBe(plan.TotalBytes);
        (await blobs.GetMetadataAsync(BlobPaths.ChunkRehydrated(firstChunkHash))).Exists.ShouldBeFalse();
        (await blobs.GetMetadataAsync(BlobPaths.ChunkRehydrated(secondChunkHash))).Exists.ShouldBeFalse();
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

    private static byte[] Compress(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(content);
        }

        return output.ToArray();
    }

    private sealed class DisposalTrackingBlobContainerService(byte[] compressedContent) : IBlobContainerService
    {
        public DisposalTrackingStream DownloadStream { get; } = new(compressedContent);

        public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default)
        {
            DownloadStream.Position = 0;
            return Task.FromResult<Stream>(DownloadStream);
        }

        public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BlobMetadata { Exists = blobName == BlobPaths.Chunk(TestChunkHash), Tier = BlobTier.Hot });

        public async IAsyncEnumerable<string> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
    }

    private sealed class DisposalTrackingEncryptionService : IEncryptionService
    {
        public DisposalTrackingStream? DecryptStream { get; private set; }

        public bool IsEncrypted => true;

        public Stream WrapForEncryption(Stream inner) => throw new NotSupportedException();

        public Stream WrapForDecryption(Stream inner)
        {
            DecryptStream = new DisposalTrackingStream(inner);
            return DecryptStream;
        }

        public ContentHash ComputeHash(byte[] data) => throw new NotSupportedException();
        public Task<ContentHash> ComputeHashAsync(Stream data, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ContentHash> ComputeHashAsync(string filePath, IProgress<long>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DisposalTrackingStream : Stream
    {
        private readonly Stream _inner;
        private int _disposed;

        public DisposalTrackingStream(byte[] content)
            : this(new MemoryStream(content, writable: false))
        {
        }

        public DisposalTrackingStream(Stream inner)
        {
            _inner = inner;
        }

        public int DisposeCount => SyncDisposeCount + AsyncDisposeCount;
        public int SyncDisposeCount { get; private set; }
        public int AsyncDisposeCount { get; private set; }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                SyncDisposeCount++;
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            AsyncDisposeCount++;
            await _inner.DisposeAsync();
        }
    }

}
