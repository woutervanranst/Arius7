using Arius.Core.Shared.Storage;

namespace Arius.Integration.Tests.Pipeline.Fakes;

/// <summary>
/// Wraps an <see cref="IBlobContainerService"/> and throws after <paramref name="throwAfterN"/>
/// successful upload completions. Large/tar chunk uploads are counted when metadata is written;
/// direct <see cref="IBlobContainerService.UploadAsync"/> calls are counted directly.
/// Used to simulate crashes mid-pipeline.
/// </summary>
internal sealed class FaultingBlobService(IBlobContainerService inner, int throwAfterN) : IBlobContainerService
{
    private int _uploadCount;

    private bool ShouldFaultUpload() => Interlocked.Increment(ref _uploadCount) > throwAfterN;

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default)
        => inner.CreateContainerIfNotExistsAsync(cancellationToken);

    public async Task<UploadResult> UploadAsync(
        RelativePath blobName,
        Stream content,
        IReadOnlyDictionary<string, string> metadata,
        BlobTier tier,
        string? contentType = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (ShouldFaultUpload())
            throw new IOException($"Fault-injected failure while uploading {blobName}");

        return await inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);
    }

    public Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => inner.DownloadAsync(blobName, cancellationToken);

    public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => inner.TryDownloadAsync(blobName, cancellationToken);

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => inner.GetMetadataAsync(blobName, cancellationToken);

    public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata = false, CancellationToken cancellationToken = default)
        => inner.ListAsync(prefix, includeMetadata, cancellationToken);

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        if (ShouldFaultUpload())
            throw new IOException($"Fault-injected failure while writing metadata for {blobName}");

        return inner.SetMetadataAsync(blobName, metadata, cancellationToken);
    }

    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default)
        => inner.SetTierAsync(blobName, tier, cancellationToken);

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null,
        CancellationToken cancellationToken = default)
        => inner.OpenWriteAsync(blobName, contentType, cancellationToken);

    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier,
        RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default)
        => inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => inner.DeleteAsync(blobName, cancellationToken);
}
