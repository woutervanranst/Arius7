using System.Runtime.CompilerServices;
using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Fakes;

/// <summary>
/// Wraps a <see cref="FakeInMemoryBlobContainerService"/> and faults the FIRST chunk-index listing (modelling a
/// transient Azure list error that outlasts the SDK's retry budget), delegating every later call to
/// <see cref="Inner"/>. Lets a test prove the run-scoped listing cache re-lists and recovers on a later lookup
/// instead of pinning the faulted task.
/// </summary>
internal sealed class FaultOnceListBlobContainerService(FakeInMemoryBlobContainerService inner) : IBlobContainerService
{
    private int _chunkIndexListAttempts;

    /// <summary>The shared underlying store; seed and inspect through this from the test.</summary>
    public FakeInMemoryBlobContainerService Inner { get; } = inner;

    public async IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, BlobListPrefixKind prefixKind = BlobListPrefixKind.DirectoryPrefix, bool includeMetadata = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (prefix.StartsWith(BlobPaths.ChunkIndexPrefix) && Interlocked.Increment(ref _chunkIndexListAttempts) == 1)
            throw new InvalidOperationException("simulated transient chunk-index list failure");

        await foreach (var item in Inner.ListAsync(prefix, prefixKind, includeMetadata, cancellationToken))
            yield return item;
    }

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default)
        => Inner.CreateContainerIfNotExistsAsync(cancellationToken);

    public Task<UploadResult> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
        => Inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default)
        => Inner.OpenWriteAsync(blobName, contentType, cancellationToken);

    public Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => Inner.DownloadAsync(blobName, cancellationToken);

    public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => Inner.TryDownloadAsync(blobName, cancellationToken);

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => Inner.GetMetadataAsync(blobName, cancellationToken);

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        => Inner.SetMetadataAsync(blobName, metadata, cancellationToken);

    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default)
        => Inner.SetTierAsync(blobName, tier, cancellationToken);

    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default)
        => Inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => Inner.DeleteAsync(blobName, cancellationToken);
}
