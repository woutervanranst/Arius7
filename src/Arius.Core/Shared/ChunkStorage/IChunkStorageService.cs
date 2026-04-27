using Arius.Core.Shared.Storage;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Shared.ChunkStorage;

/// <summary>
/// Owns chunk blob operations that feature handlers orchestrate: upload, download,
/// hydration state, rehydration start, and cleanup planning.
/// </summary>
public interface IChunkStorageService
{
    /// <summary>
    /// Uploads a large-file chunk blob and returns the stored chunk metadata needed for index recording.
    /// </summary>
    Task<ChunkUploadResult> UploadLargeAsync(
        ChunkHash chunkHash,
        Stream content,
        long sourceSize,
        BlobTier tier,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a tar bundle chunk blob and returns the stored chunk metadata needed for proportional thin entries.
    /// </summary>
    Task<ChunkUploadResult> UploadTarAsync(
        ChunkHash chunkHash,
        Stream content,
        long sourceSize,
        BlobTier tier,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a thin chunk that points a content hash at an existing parent tar chunk.
    /// </summary>
    Task<bool> UploadThinAsync(
        ContentHash contentHash,
        ChunkHash parentChunkHash,
        long originalSize,
        long compressedSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the readable form of a chunk as a plaintext payload stream, preferring a ready rehydrated copy when present.
    /// </summary>
    Task<Stream> DownloadAsync(
        ChunkHash chunkHash,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves whether a chunk is directly available, needs rehydration, is pending rehydration, or is unknown.
    /// </summary>
    Task<ChunkHydrationStatus> GetHydrationStatusAsync(
        ChunkHash chunkHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts rehydration for an archived chunk by creating or refreshing its rehydrated copy.
    /// </summary>
    Task StartRehydrationAsync(
        ChunkHash chunkHash,
        RehydratePriority priority,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a cleanup plan for rehydrated chunk blobs so callers can preview and then execute deletion.
    /// </summary>
    Task<IRehydratedChunkCleanupPlan> PlanRehydratedCleanupAsync(
        CancellationToken cancellationToken = default);
}
