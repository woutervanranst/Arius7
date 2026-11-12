using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.ChunkStorage;

/// <summary>
/// Manages chunk storage and rehydration for feature handlers.
/// </summary>
/// <remarks>
/// Large and tar chunk content must remain unchanged from hashing through upload completion.
/// </remarks>
public interface IChunkStorageService
{
    /// <summary>
    /// Returns the metadata needed to record the chunk in the chunk index.
    /// An archive-tier request keeps chunks whose stored size is at or below <paramref name="smallFileThreshold"/> online;
    /// <see cref="ChunkUploadResult.ActualTier"/> reports the stored tier.
    /// </summary>
    Task<ChunkUploadResult> UploadLargeAsync(
        ChunkHash chunkHash,
        Stream content,
        long sourceSize,
        BlobTier tier,
        long smallFileThreshold,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the metadata needed to create thin chunks.
    /// Applies the same archive-tier size policy as <see cref="UploadLargeAsync"/>.
    /// </summary>
    Task<ChunkUploadResult> UploadTarAsync(
        ChunkHash chunkHash,
        Stream content,
        long sourceSize,
        BlobTier tier,
        long smallFileThreshold,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a thin chunk that points a content hash at an existing parent tar chunk.
    /// </summary>
    Task<bool> UploadThinAsync(
        ContentHash contentHash,
        ChunkHash parentChunkHash,
        long originalSize,
        long chunkSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a plaintext payload stream, preferring a readable rehydrated copy when present.
    /// </summary>
    Task<Stream> DownloadAsync(
        ChunkHash chunkHash,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Maps chunk hashes to rehydrated-copy readiness: <c>true</c> means readable,
    /// and <c>false</c> means rehydration is incomplete.
    /// </summary>
    Task<IReadOnlyDictionary<ChunkHash, bool>> ListRehydratedChunksAsync(
        CancellationToken cancellationToken = default);
}
