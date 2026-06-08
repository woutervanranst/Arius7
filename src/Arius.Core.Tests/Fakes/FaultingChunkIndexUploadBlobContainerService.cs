using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Fakes;

/// <summary>
/// Wraps a <see cref="FakeInMemoryBlobContainerService"/> and fails chunk-index shard uploads, so tests can
/// simulate an interrupted archive whose flush dies after some chunks were already uploaded. Every other call
/// (downloads, seeding, listing, tracking) is delegated to the shared <see cref="Inner"/> instance, so a test
/// can keep recording/seeding through that same reference and then retry the flush through a non-faulting view.
/// </summary>
internal sealed class FaultingChunkIndexUploadBlobContainerService(FakeInMemoryBlobContainerService inner) : IBlobContainerService
{
    /// <summary>The shared underlying store; seed and inspect through this from the test.</summary>
    public FakeInMemoryBlobContainerService Inner { get; } = inner;

    /// <summary>When <see langword="true"/>, uploads to <c>chunk-index/*</c> throw instead of being stored.</summary>
    public bool FailChunkIndexUploads { get; set; } = true;

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default)
        => Inner.CreateContainerIfNotExistsAsync(cancellationToken);

    public Task<UploadResult> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
        => FailChunkIndexUploads && blobName.StartsWith(BlobPaths.ChunkIndexPrefix)
            ? throw new InvalidOperationException("chunk-index upload failed")
            : Inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default)
        => Inner.OpenWriteAsync(blobName, contentType, cancellationToken);

    public Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => Inner.DownloadAsync(blobName, cancellationToken);

    public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => Inner.TryDownloadAsync(blobName, cancellationToken);

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => Inner.GetMetadataAsync(blobName, cancellationToken);

    public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata = false, CancellationToken cancellationToken = default)
        => Inner.ListAsync(prefix, includeMetadata, cancellationToken);

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        => Inner.SetMetadataAsync(blobName, metadata, cancellationToken);

    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default)
        => Inner.SetTierAsync(blobName, tier, cancellationToken);

    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default)
        => Inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => Inner.DeleteAsync(blobName, cancellationToken);
}
