using System.IO.Compression;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Storage;
using Arius.Core.Shared.Streaming;

namespace Arius.Core.Shared.ChunkStorage;

public sealed class ChunkStorageService : IChunkStorageService
{
    private readonly IBlobContainerService _blobs;
    private readonly IEncryptionService _encryption;

    public ChunkStorageService(IBlobContainerService blobs, IEncryptionService encryption)
    {
        _blobs = blobs;
        _encryption = encryption;
    }

    public Task<ChunkUploadResult> UploadLargeAsync(
        ChunkHash chunkHash,
        Stream content,
        long sourceSize,
        BlobTier tier,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        UploadChunkAsync(
            chunkHash,
            content,
            sourceSize,
            tier,
            progress,
            BlobMetadataKeys.TypeLarge,
            isTar: false,
            cancellationToken);

    public Task<ChunkUploadResult> UploadTarAsync(
        ChunkHash chunkHash,
        Stream content,
        long sourceSize,
        BlobTier tier,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        UploadChunkAsync(
            chunkHash,
            content,
            sourceSize,
            tier,
            progress,
            BlobMetadataKeys.TypeTar,
            isTar: true,
            cancellationToken);

    public Task<bool> UploadThinAsync(
        ContentHash contentHash,
        ChunkHash parentChunkHash,
        long originalSize,
        long compressedSize,
        CancellationToken cancellationToken = default) =>
        UploadThinCoreAsync(contentHash, parentChunkHash, originalSize, compressedSize, cancellationToken);

    public Task<Stream> DownloadAsync(
        ChunkHash chunkHash,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadCoreAsync(chunkHash, progress, cancellationToken);

    public Task<ChunkHydrationStatus> GetHydrationStatusAsync(
        ChunkHash chunkHash,
        CancellationToken cancellationToken = default) =>
        GetHydrationStatusCoreAsync(chunkHash, cancellationToken);

    public Task StartRehydrationAsync(
        ChunkHash chunkHash,
        RehydratePriority priority,
        CancellationToken cancellationToken = default) =>
        _blobs.CopyAsync(BlobPaths.Chunk(chunkHash), BlobPaths.ChunkRehydrated(chunkHash), BlobTier.Cold, priority, cancellationToken);

    public Task<IRehydratedChunkCleanupPlan> PlanRehydratedCleanupAsync(
        CancellationToken cancellationToken = default) =>
        PlanCleanupCoreAsync(cancellationToken);

    private async Task<ChunkUploadResult> UploadChunkAsync(
        ChunkHash chunkHash,
        Stream content,
        long sourceSize,
        BlobTier tier,
        IProgress<long>? progress,
        string ariusType,
        bool isTar,
        CancellationToken cancellationToken)
    {
        var blobName = BlobPaths.Chunk(chunkHash);
        var contentType = GetChunkContentType(isTar);

        if (!content.CanSeek)
            throw new InvalidOperationException("Chunk uploads require a seekable source stream so retries can rewind the content.");

        if (content.Position != 0)
            content.Position = 0;

        long reportedBytes = 0;

        retry:
        try
        {
            long storedSize;

            await using (var writeStream = await _blobs.OpenWriteAsync(blobName, contentType, cancellationToken))
            {
                var countingStream = new CountingStream(writeStream);
                await using var encryptionStream = _encryption.WrapForEncryption(countingStream);
                await using var gzipStream = new GZipStream(encryptionStream, CompressionLevel.Optimal, leaveOpen: true);
                var progressStream = progress is null
                    ? null
                    : new ProgressStream(content, new CallbackProgress(bytesRead =>
                    {
                        if (bytesRead <= reportedBytes)
                            return;

                        reportedBytes = bytesRead;
                        progress.Report(bytesRead);
                    }));

                var source = progressStream ?? content;
                await source.CopyToAsync(gzipStream, cancellationToken);

                await gzipStream.DisposeAsync();
                await encryptionStream.DisposeAsync();
                storedSize = countingStream.BytesWritten;
            }

            var metadata = new Dictionary<string, string>
            {
                [BlobMetadataKeys.AriusType] = ariusType,
                [BlobMetadataKeys.ChunkSize] = storedSize.ToString(),
            };

            if (!isTar) // On TAR blobs this doesnt make sense. We find the original size on the respective thin chunks.
                metadata[BlobMetadataKeys.OriginalSize] = sourceSize.ToString();

            await _blobs.SetMetadataAsync(blobName, metadata, cancellationToken);
            await _blobs.SetTierAsync(blobName, tier, cancellationToken);

            return new ChunkUploadResult(chunkHash, storedSize, AlreadyExisted: false);
        }
        catch (BlobAlreadyExistsException)
        {
            // DESIGN DECISION: The Metadata is written only after a succesful upload, so we can assume that if the blob has this metadata, the upload completed successfully
            var existing = await _blobs.GetMetadataAsync(blobName, cancellationToken);
            if (existing.Metadata.ContainsKey(BlobMetadataKeys.AriusType))
                return new ChunkUploadResult(chunkHash, existing.ContentLength ?? 0, AlreadyExisted: true);

            await _blobs.DeleteAsync(blobName, cancellationToken);
            content.Position = 0;
            goto retry;
        }
    }

    private string GetChunkContentType(bool isTar)
    {
        if (_encryption.IsEncrypted)
            return isTar ? ContentTypes.TarGcmEncrypted : ContentTypes.LargeGcmEncrypted;

        return isTar ? ContentTypes.TarPlaintext : ContentTypes.LargePlaintext;
    }

    private async Task<bool> UploadThinCoreAsync(
        ContentHash contentHash,
        ChunkHash parentChunkHash,
        long originalSize,
        long compressedSize,
        CancellationToken cancellationToken)
    {
        var blobName = BlobPaths.ThinChunk(contentHash);
        var metadata = new Dictionary<string, string>
        {
            [BlobMetadataKeys.AriusType] = BlobMetadataKeys.TypeThin,
            [BlobMetadataKeys.OriginalSize] = originalSize.ToString(),
            [BlobMetadataKeys.CompressedSize] = compressedSize.ToString(),
        };

    retry:
        try
        {
            await _blobs.UploadAsync(
                blobName: blobName,
                content: new MemoryStream(System.Text.Encoding.UTF8.GetBytes(parentChunkHash.ToString())),
                metadata: new Dictionary<string, string>(),
                tier: BlobTier.Cool,
                contentType: ContentTypes.Thin,
                overwrite: false,
                cancellationToken: cancellationToken);

            await _blobs.SetMetadataAsync(blobName, metadata, cancellationToken);
            return true;
        }
        catch (BlobAlreadyExistsException)
        {
            var existing = await _blobs.GetMetadataAsync(blobName, cancellationToken);
            if (existing.Metadata.ContainsKey(BlobMetadataKeys.AriusType))
                return false;

            await _blobs.DeleteAsync(blobName, cancellationToken);
            goto retry;
        }
    }

    private async Task<Stream> DownloadCoreAsync(ChunkHash chunkHash, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var blobName = await SelectReadableChunkBlobAsync(chunkHash, cancellationToken);
        var downloadStream = await _blobs.DownloadAsync(blobName, cancellationToken);
        var progressOrRawStream = progress is null ? downloadStream : new ProgressStream(downloadStream, progress);
        var decryptStream = _encryption.WrapForDecryption(progressOrRawStream);
        var gzipStream = new GZipStream(decryptStream, CompressionMode.Decompress);
        return new ChunkDownloadStream(gzipStream, decryptStream, progressOrRawStream, downloadStream);
    }

    private async Task<ChunkHydrationStatus> GetHydrationStatusCoreAsync(ChunkHash chunkHash, CancellationToken cancellationToken)
    {
        var chunkMeta = await _blobs.GetMetadataAsync(BlobPaths.Chunk(chunkHash), cancellationToken).ConfigureAwait(false);
        if (!chunkMeta.Exists)
            return ChunkHydrationStatus.Unknown;

        if (chunkMeta.Tier != BlobTier.Archive)
            return ChunkHydrationStatus.Available;

        var rehydratedMeta = await _blobs.GetMetadataAsync(BlobPaths.ChunkRehydrated(chunkHash), cancellationToken).ConfigureAwait(false);
        if (rehydratedMeta.Exists)
            return rehydratedMeta.Tier == BlobTier.Archive
                ? ChunkHydrationStatus.RehydrationPending
                : ChunkHydrationStatus.Available;

        return chunkMeta.IsRehydrating
            ? ChunkHydrationStatus.RehydrationPending
            : ChunkHydrationStatus.NeedsRehydration;
    }

    private async Task<string> SelectReadableChunkBlobAsync(ChunkHash chunkHash, CancellationToken cancellationToken)
    {
        var rehydratedMeta = await _blobs.GetMetadataAsync(BlobPaths.ChunkRehydrated(chunkHash), cancellationToken);
        if (rehydratedMeta.Exists && rehydratedMeta.Tier != BlobTier.Archive)
            return BlobPaths.ChunkRehydrated(chunkHash);

        return BlobPaths.Chunk(chunkHash);
    }

    private async Task<IRehydratedChunkCleanupPlan> PlanCleanupCoreAsync(CancellationToken cancellationToken)
    {
        var blobNames = new List<string>();
        long totalBytes = 0;

        await foreach (var blobName in _blobs.ListAsync(BlobPaths.ChunksRehydrated, cancellationToken))
        {
            blobNames.Add(blobName);
            var meta = await _blobs.GetMetadataAsync(blobName, cancellationToken);
            totalBytes += meta.ContentLength ?? 0;
        }

        return new RehydratedChunkCleanupPlan(_blobs, blobNames, totalBytes);
    }

    private sealed class RehydratedChunkCleanupPlan : IRehydratedChunkCleanupPlan
    {
        private const int DeleteWorkers = 16;

        private readonly IBlobContainerService _blobs;
        private readonly IReadOnlyList<string> _blobNames;

        public RehydratedChunkCleanupPlan(IBlobContainerService blobs, IReadOnlyList<string> blobNames, long totalBytes)
        {
            _blobs = blobs;
            _blobNames = blobNames;
            TotalBytes = totalBytes;
        }

        public int ChunkCount => _blobNames.Count;

        public long TotalBytes { get; }

        public async Task<RehydratedChunkCleanupResult> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            var deleted = 0;
            long freed = 0;

            await Parallel.ForEachAsync(_blobNames, new ParallelOptions { MaxDegreeOfParallelism = DeleteWorkers, CancellationToken = cancellationToken },
                async (blobName, ct) =>
                {
                    var meta = await _blobs.GetMetadataAsync(blobName, ct);
                    await _blobs.DeleteAsync(blobName, ct);

                    Interlocked.Increment(ref deleted);
                    Interlocked.Add(ref freed, meta.ContentLength ?? 0);
                });

            return new RehydratedChunkCleanupResult(deleted, freed);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CallbackProgress(Action<long> callback) : IProgress<long>
    {
        public void Report(long value) => callback(value);
    }

    /// <summary>
    /// Wraps the fully constructed chunk download pipeline returned by <see cref="DownloadCoreAsync"/>.
    /// Unlike <see cref="ProgressStream"/> and <see cref="CountingStream"/>, this is not a reusable behavior wrapper
    /// that adds a cross-cutting read/write concern. Its only job is to expose the final readable stream while owning
    /// disposal of the chunk-specific stack underneath it: decompression, decryption, optional progress reporting, and
    /// the underlying blob download stream.
    /// </summary>
    private sealed class ChunkDownloadStream : Stream
    {
        private readonly Stream _inner;
        private int _disposed;

        public ChunkDownloadStream(Stream inner, Stream decryptStream, Stream progressStream, Stream downloadStream)
        {
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override ValueTask DisposeAsync() => DisposeCoreAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                DisposeResources();

            base.Dispose(disposing);
        }

        private async ValueTask DisposeCoreAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await _inner.DisposeAsync();

            GC.SuppressFinalize(this);
        }

        private void DisposeResources()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _inner.Dispose();
        }
    }
}
