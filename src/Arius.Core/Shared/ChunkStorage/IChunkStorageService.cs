using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.ChunkStorage;

public interface IChunkStorageService
{
    Task<ChunkUploadResult> UploadLargeAsync(
        string chunkHash,
        Stream content,
        long sourceSize,
        BlobTier tier,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ChunkUploadResult> UploadTarAsync(
        string chunkHash,
        Stream content,
        long sourceSize,
        BlobTier tier,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> UploadThinAsync(
        string contentHash,
        string parentChunkHash,
        long originalSize,
        long compressedSize,
        CancellationToken cancellationToken = default);

    Task<Stream> DownloadAsync(
        string chunkHash,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ChunkHydrationStatus> GetHydrationStatusAsync(
        string chunkHash,
        CancellationToken cancellationToken = default);

    Task StartRehydrationAsync(
        string chunkHash,
        RehydratePriority priority,
        CancellationToken cancellationToken = default);

    Task<IRehydratedChunkCleanupPlan> PlanRehydratedCleanupAsync(
        CancellationToken cancellationToken = default);
}
