using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;
using Arius.Core.Shared.Streaming;
using System.IO.Compression;

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
        string chunkHash,
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
        string chunkHash,
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
        string contentHash,
        string parentChunkHash,
        long originalSize,
        long compressedSize,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<Stream> DownloadAsync(
        string chunkHash,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<ChunkHydrationStatus> GetHydrationStatusAsync(
        string chunkHash,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task StartRehydrationAsync(
        string chunkHash,
        RehydratePriority priority,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<IRehydratedChunkCleanupPlan> PlanRehydratedCleanupAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    private async Task<ChunkUploadResult> UploadChunkAsync(
        string chunkHash,
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

    retry:
        try
        {
            long storedSize;

            await using (var writeStream = await _blobs.OpenWriteAsync(blobName, contentType, cancellationToken))
            {
                var countingStream = new CountingStream(writeStream);
                await using var encryptionStream = _encryption.WrapForEncryption(countingStream);
                await using var gzipStream = new GZipStream(encryptionStream, CompressionLevel.Optimal, leaveOpen: true);
                await using var progressStream = progress is null ? null : new ProgressStream(content, progress);

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

            if (!isTar)
                metadata[BlobMetadataKeys.OriginalSize] = sourceSize.ToString();

            await _blobs.SetMetadataAsync(blobName, metadata, cancellationToken);
            await _blobs.SetTierAsync(blobName, tier, cancellationToken);

            return new ChunkUploadResult(chunkHash, storedSize, AlreadyExisted: false);
        }
        catch (BlobAlreadyExistsException)
        {
            var existing = await _blobs.GetMetadataAsync(blobName, cancellationToken);
            if (existing.Metadata.ContainsKey(BlobMetadataKeys.AriusType))
                return new ChunkUploadResult(chunkHash, existing.ContentLength ?? 0, AlreadyExisted: true);

            await _blobs.DeleteAsync(blobName, cancellationToken);
            if (content.CanSeek)
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
}
