using System.IO.Compression;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;
using Arius.Core.Shared.Streaming;

namespace Arius.Core.Shared.ChunkStorage;

[SharedWithinAssembly]
internal sealed class ChunkStorageService(IBlobContainerService blobs, IEncryptionService encryption) : IChunkStorageService
{
    // --- UPLOAD 

    public Task<ChunkUploadResult> UploadLargeAsync(ChunkHash chunkHash, Stream content, long sourceSize, BlobTier tier, IProgress<long>? progress = null, CancellationToken cancellationToken = default) 
        => UploadChunkAsync(chunkHash, content, sourceSize, tier, progress, BlobMetadataKeys.TypeLarge, isTar: false, cancellationToken);

    public Task<ChunkUploadResult> UploadTarAsync(ChunkHash chunkHash, Stream content, long sourceSize, BlobTier tier, IProgress<long>? progress = null, CancellationToken cancellationToken = default) 
        => UploadChunkAsync(chunkHash, content, sourceSize, tier, progress, BlobMetadataKeys.TypeTar, isTar: true, cancellationToken);

    public async Task<bool> UploadThinAsync(ContentHash contentHash, ChunkHash parentChunkHash, long originalSize, long chunkSize, CancellationToken cancellationToken = default)
    {
        var blobName = BlobPaths.ThinChunkPath(contentHash);
        var metadata = new Dictionary<string, string>
        {
            [BlobMetadataKeys.AriusType]       = BlobMetadataKeys.TypeThin,
            [BlobMetadataKeys.ParentChunkHash] = parentChunkHash.ToString(),
            [BlobMetadataKeys.OriginalSize]    = originalSize.ToString(),
            [BlobMetadataKeys.ChunkSize]       = chunkSize.ToString(),
        };

        retry:
        try
        {
            await blobs.UploadAsync(
                blobName: blobName,
                content: new MemoryStream([], writable: false),
                metadata: metadata,
                tier: BlobTier.Cool,
                contentType: ContentTypes.Thin,
                overwrite: false,
                cancellationToken: cancellationToken);

            return true;
        }
        catch (BlobAlreadyExistsException)
        {
            var existing = await blobs.GetMetadataAsync(blobName, cancellationToken);
            if (existing.Metadata.ContainsKey(BlobMetadataKeys.AriusType))
                return false;

            await blobs.DeleteAsync(blobName, cancellationToken);
            goto retry;
        }
    }

    private async Task<ChunkUploadResult> UploadChunkAsync(ChunkHash chunkHash, Stream content, long sourceSize, BlobTier tier, IProgress<long>? progress, string ariusType, bool isTar, CancellationToken cancellationToken)
    {
        var blobName = BlobPaths.ChunkPath(chunkHash);
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

            await using (var writeStream = await blobs.OpenWriteAsync(blobName, contentType, cancellationToken))
            {
                var countingStream = new CountingStream(writeStream);
                await using var encryptionStream = encryption.WrapForEncryption(countingStream);
                await using var gzipStream = new GZipStream(encryptionStream, CompressionLevel.SmallestSize, leaveOpen: true);
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

                await gzipStream.DisposeAsync(); // NOTE: leave Dispose to get a correct BytesWritten
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

            await blobs.SetMetadataAsync(blobName, metadata, cancellationToken);
            await blobs.SetTierAsync(blobName, tier, cancellationToken);

            return new ChunkUploadResult(chunkHash, storedSize, AlreadyExisted: false, OriginalSize: sourceSize);
        }
        catch (BlobAlreadyExistsException)
        {
            // DESIGN DECISION: The Metadata is written only after a successful upload, so we can assume that if the blob has this metadata, the upload completed successfully
            var existing = await blobs.GetMetadataAsync(blobName, cancellationToken);
            if (existing.Metadata.ContainsKey(BlobMetadataKeys.AriusType))
                return new ChunkUploadResult(chunkHash, existing.ContentLength ?? 0, AlreadyExisted: true, OriginalSize: TryReadOriginalSize(existing.Metadata));

            await blobs.DeleteAsync(blobName, cancellationToken);
            content.Position = 0;
            goto retry;
        }


        string GetChunkContentType(bool isTar)
        {
            if (encryption.IsEncrypted)
                return isTar ? ContentTypes.TarGcmEncrypted : ContentTypes.LargeGcmEncrypted;

            return isTar ? ContentTypes.TarPlaintext : ContentTypes.LargePlaintext;
        }

        static long? TryReadOriginalSize(IReadOnlyDictionary<string, string> metadata)
            => metadata.TryGetValue(BlobMetadataKeys.OriginalSize, out var value) && long.TryParse(value, out var originalSize)
                ? originalSize
                : null;
    }

    
    // --- DOWNLOAD
    
    public Task<Stream> DownloadAsync(ChunkHash chunkHash, IProgress<long>? progress = null, CancellationToken cancellationToken = default) 
        => DownloadCoreAsync(chunkHash, progress, cancellationToken);
    
    public async Task<ChunkHydrationStatus> GetHydrationStatusAsync(ChunkHash chunkHash, CancellationToken cancellationToken = default)
    {
        var chunkMeta = await blobs.GetMetadataAsync(BlobPaths.ChunkPath(chunkHash), cancellationToken).ConfigureAwait(false);
        if (!chunkMeta.Exists)
            return ChunkHydrationStatus.Unknown;

        if (chunkMeta.Tier != BlobTier.Archive)
            return ChunkHydrationStatus.Available;

        var rehydratedMeta = await blobs.GetMetadataAsync(BlobPaths.ChunkRehydratedPath(chunkHash), cancellationToken).ConfigureAwait(false);
        if (rehydratedMeta.Exists)
            return rehydratedMeta.Tier == BlobTier.Archive
                ? ChunkHydrationStatus.RehydrationPending
                : ChunkHydrationStatus.Available;

        return chunkMeta.IsRehydrating
            ? ChunkHydrationStatus.RehydrationPending
            : ChunkHydrationStatus.NeedsRehydration;
    }

    public Task StartRehydrationAsync(ChunkHash chunkHash, RehydratePriority priority, CancellationToken cancellationToken = default) 
        => blobs.CopyAsync(BlobPaths.ChunkPath(chunkHash), BlobPaths.ChunkRehydratedPath(chunkHash), BlobTier.Cold, priority, cancellationToken);

    public async Task<IRehydratedChunkCleanupPlan> PlanRehydratedCleanupAsync(CancellationToken cancellationToken = default)
    {
        var  blobNames  = new List<RelativePath>();
        long totalBytes = 0;

        await foreach (var item in blobs.ListAsync(BlobPaths.ChunksRehydratedPrefix, includeMetadata: true, cancellationToken: cancellationToken))
        {
            var blobName = item.Name;
            blobNames.Add(blobName);
            totalBytes += item.ContentLength ?? 0;
        }

        return new RehydratedChunkCleanupPlan(blobs, blobNames, totalBytes);
    }

    private async Task<Stream> DownloadCoreAsync(ChunkHash chunkHash, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var blobName            = await SelectReadableChunkBlobAsync(chunkHash, cancellationToken);
        var download            = await blobs.DownloadAsync(blobName, cancellationToken);
        var progressOrRawStream = progress is null ? download.Stream : new ProgressStream(download.Stream, progress);
        var decryptStream       = encryption.WrapForDecryption(progressOrRawStream);
        var gzipStream          = new GZipStream(decryptStream, CompressionMode.Decompress);
        return new ChunkDownloadStream(gzipStream);


        async Task<RelativePath> SelectReadableChunkBlobAsync(ChunkHash chunkHash, CancellationToken cancellationToken)
        {
            var rehydratedPath = BlobPaths.ChunkRehydratedPath(chunkHash);
            var rehydratedMeta = await blobs.GetMetadataAsync(rehydratedPath, cancellationToken);
            if (rehydratedMeta.Exists && rehydratedMeta.Tier != BlobTier.Archive)
                return rehydratedPath;

            return BlobPaths.ChunkPath(chunkHash);
        }
    }


    // -- LIST

    public async Task<IReadOnlyDictionary<ChunkHash, bool>> ListRehydratedChunksAsync(CancellationToken cancellationToken = default)
    {
        var rehydrated = new Dictionary<ChunkHash, bool>();
        await foreach (var item in blobs.ListAsync(BlobPaths.ChunksRehydratedPrefix, includeMetadata: false, cancellationToken: cancellationToken))
        {
            // The rehydrated blob name is "chunks-rehydrated/{chunkHash}"; the final segment is the hash.
            if (!ChunkHash.TryParse(item.Name.Name.ToString(), out var chunkHash))
                continue;

            // Tier != Archive → the copy is hydrated and ready to download; Archive → still rehydrating.
            rehydrated[chunkHash] = item.Tier is not null && item.Tier != BlobTier.Archive;
        }

        return rehydrated;
    }


    // --- HELPER CLASSES

    private sealed class RehydratedChunkCleanupPlan(IBlobContainerService blobs, IReadOnlyList<RelativePath> blobNames, long totalBytes) : IRehydratedChunkCleanupPlan
    {
        private const int DeleteWorkers = 16;

        public int ChunkCount => blobNames.Count;

        public long TotalBytes { get; } = totalBytes;

        public async Task<RehydratedChunkCleanupResult> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            var deleted = 0;
            long freed = 0;

            await Parallel.ForEachAsync(blobNames, new ParallelOptions { MaxDegreeOfParallelism = DeleteWorkers, CancellationToken = cancellationToken },
                async (blobName, ct) =>
                {
                    var meta = await blobs.GetMetadataAsync(blobName, ct);
                    await blobs.DeleteAsync(blobName, ct);

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
    private sealed class ChunkDownloadStream(Stream inner) : Stream
    {
        private          int    _disposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
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

            await inner.DisposeAsync();

            GC.SuppressFinalize(this);
        }

        private void DisposeResources()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            inner.Dispose();
        }
    }
}
