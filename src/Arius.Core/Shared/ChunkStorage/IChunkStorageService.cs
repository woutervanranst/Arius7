using Arius.Core.Shared.Storage;

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
        string chunkHash,
        Stream content,
        long sourceSize,
        BlobTier tier,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a tar bundle chunk blob and returns the stored chunk metadata needed for proportional thin entries.
    /// </summary>
    Task<ChunkUploadResult> UploadTarAsync(
        string chunkHash,
        Stream content,
        long sourceSize,
        BlobTier tier,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a thin chunk that points a content hash at an existing parent tar chunk.
    /// </summary>
    Task<bool> UploadThinAsync(
        string contentHash,
        string parentChunkHash,
        long originalSize,
        long compressedSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the readable form of a chunk as a plaintext payload stream, preferring a ready rehydrated copy when present.
    /// </summary>
    Task<Stream> DownloadAsync(
        string chunkHash,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves whether a chunk is directly available, needs rehydration, is pending rehydration, or is unknown.
    /// </summary>
    Task<ChunkHydrationStatus> GetHydrationStatusAsync(
        string chunkHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts rehydration for an archived chunk by creating or refreshing its rehydrated copy.
    /// </summary>
    Task StartRehydrationAsync(
        string chunkHash,
        RehydratePriority priority,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a cleanup plan for rehydrated chunk blobs so callers can preview and then execute deletion.
    /// </summary>
    Task<IRehydratedChunkCleanupPlan> PlanRehydratedCleanupAsync(
        CancellationToken cancellationToken = default);
}
