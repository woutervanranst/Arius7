using System.IO.Pipelines;
using Arius.Core.Shared.Compression;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;
using Arius.Core.Shared.Streaming;

namespace Arius.Core.Shared.ChunkStorage;

[SharedWithinAssembly]
internal sealed class ChunkStorageService(IBlobContainerService blobs, IEncryptionService encryption, ICompressionService compression) : IChunkStorageService
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
            ContentHash verifiedHash;

            await using (var writeStream = await blobs.OpenWriteAsync(blobName, contentType, cancellationToken))
            {
                var countingStream = new CountingStream(writeStream);
                await using var encryptionStream = encryption.WrapForEncryption(countingStream);

                // Inline round-trip verification: tee the compressed bytes to (a) the encrypt→upload chain
                // and (b) a decompress→hash pipe, so we prove the chunk is restorable before recording it.
                await using var verifier = new RoundTripVerifier(compression, encryption, cancellationToken);
                var teeStream         = new TeeStream(encryptionStream, verifier.Sink);
                var compressionStream = compression.WrapForCompression(teeStream, leaveOpen: false);

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
                await source.CopyToAsync(compressionStream, cancellationToken);

                await compressionStream.DisposeAsync();  // finalize the zstd frame → tee → encryption + verifier
                verifiedHash = await verifier.CompleteAsync(cancellationToken); // hash of the decompressed bytes
                await encryptionStream.DisposeAsync();   // NOTE: leave Dispose to get a correct BytesWritten
                storedSize = countingStream.BytesWritten;
            }

            if (verifiedHash != ContentHash.Parse(chunkHash))
            {
                // Compression did not round-trip — the stored frame is not restorable. Fail loudly and
                // remove the unusable blob rather than recording an unrecoverable chunk.
                await blobs.DeleteAsync(blobName, cancellationToken);
                throw new InvalidDataException(
                    $"Chunk {chunkHash.Short8} failed compression round-trip verification " +
                    $"(restored hash {verifiedHash.Short8} ≠ {chunkHash.Short8}); the blob was not recorded.");
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
        var decompressStream    = compression.WrapForDecompression(decryptStream);
        return new ChunkDownloadStream(decompressStream);


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

            await Parallel.ForEachAsync(blobNames, new ParallelOptions { MaxDegreeOfParallelism = DeleteWorkers, CancellationToken = cancellationToken },
                async (blobName, ct) =>
                {
                    await blobs.DeleteAsync(blobName, ct);

                    Interlocked.Increment(ref deleted);
                });

            return new RehydratedChunkCleanupResult(deleted);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CallbackProgress(Action<long> callback) : IProgress<long>
    {
        public void Report(long value) => callback(value);
    }

    /// <summary>
    /// Write-only stream that forwards every write to two destinations: the primary (encrypt→upload)
    /// and the round-trip verifier's sink. Lets us verify the compressed output while it streams to
    /// blob storage, without re-reading the source or buffering the whole chunk. Both targets are left
    /// open — the caller disposes the primary (for byte counting) and the verifier owns the sink.
    /// </summary>
    private sealed class TeeStream : Stream
    {
        private readonly Stream _primary;
        private readonly Stream _secondary;

        public TeeStream(Stream primary, Stream secondary)
        {
            _primary   = primary;
            _secondary = secondary;
        }

        public override bool CanRead  => false;
        public override bool CanSeek  => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _primary.Write(buffer);
            _secondary.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _primary.WriteAsync(buffer, cancellationToken);
            await _secondary.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush()
        {
            _primary.Flush();
            _secondary.Flush();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await _primary.FlushAsync(cancellationToken);
            await _secondary.FlushAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Decompresses the compressed bytes written to <see cref="Sink"/> on a background task and hashes
    /// the result, so an upload can confirm the chunk round-trips before recording it. A bounded pipe
    /// supplies backpressure so memory stays flat regardless of chunk size.
    /// </summary>
    private sealed class RoundTripVerifier : IAsyncDisposable
    {
        private const int PauseWriterThreshold  = 1 << 20; // ≤ ~1 MiB of compressed bytes buffered in-flight
        private const int ResumeWriterThreshold = 1 << 19;

        private readonly Pipe _pipe;
        private readonly Task<ContentHash> _hashTask;
        private bool _writerCompleted;

        public RoundTripVerifier(ICompressionService compression, IEncryptionService encryption, CancellationToken cancellationToken)
        {
            _pipe = new Pipe(new PipeOptions(
                pauseWriterThreshold:      PauseWriterThreshold,
                resumeWriterThreshold:     ResumeWriterThreshold,
                useSynchronizationContext: false));

            Sink = _pipe.Writer.AsStream(leaveOpen: true); // the writer is completed explicitly, not via Sink disposal

            _hashTask = Task.Run(async () =>
            {
                await using var reader     = _pipe.Reader.AsStream();
                await using var decompress = compression.WrapForDecompression(reader, leaveOpen: true);
                return await encryption.ComputeHashAsync(decompress, cancellationToken);
            }, cancellationToken);
        }

        /// <summary>The tee's secondary target: compressed bytes written here are decompressed and hashed.</summary>
        public Stream Sink { get; }

        public async Task<ContentHash> CompleteAsync(CancellationToken cancellationToken)
        {
            await CompleteWriterAsync();
            return await _hashTask.WaitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            // Release both ends so an early-exit/faulted path can't leave the background task hanging.
            await CompleteWriterAsync();
            try { await _hashTask; } catch { /* the real failure is surfaced by CompleteAsync */ }
            await _pipe.Reader.CompleteAsync();
        }

        private async ValueTask CompleteWriterAsync()
        {
            if (_writerCompleted)
                return;

            _writerCompleted = true;
            await _pipe.Writer.CompleteAsync();
        }
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
