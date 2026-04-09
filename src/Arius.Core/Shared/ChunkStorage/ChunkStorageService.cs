using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Encryption;
using Arius.Core.Shared.Storage;

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
        throw new NotImplementedException();

    public Task<ChunkUploadResult> UploadTarAsync(
        string chunkHash,
        Stream content,
        long sourceSize,
        BlobTier tier,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

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
}
