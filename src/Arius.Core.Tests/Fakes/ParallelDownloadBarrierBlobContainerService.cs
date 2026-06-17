using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Fakes;

/// <summary>
/// Wraps a <see cref="FakeInMemoryBlobContainerService"/> and blocks each chunk-index shard download until
/// <see cref="_expectedConcurrency"/> of them are simultaneously in flight, then releases all. If the downloads
/// run sequentially the barrier is never reached and the wait times out (failing the caller) — so a test can
/// prove the coverage downloads actually run concurrently, which a serialized in-memory fake cannot show.
/// </summary>
internal sealed class ParallelDownloadBarrierBlobContainerService(FakeInMemoryBlobContainerService inner, int expectedConcurrency) : IBlobContainerService
{
    private readonly int                   _expectedConcurrency = expectedConcurrency;
    private readonly TaskCompletionSource  _allArrived          = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private          int                   _inFlight;

    public FakeInMemoryBlobContainerService Inner { get; } = inner;

    /// <summary>True once <see cref="_expectedConcurrency"/> downloads were concurrently in flight.</summary>
    public bool ReachedExpectedConcurrency => _allArrived.Task.IsCompletedSuccessfully;

    public async Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
    {
        if (blobName.StartsWith(BlobPaths.ChunkIndexPrefix))
        {
            if (Interlocked.Increment(ref _inFlight) >= _expectedConcurrency)
                _allArrived.TrySetResult();

            await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        return await Inner.TryDownloadAsync(blobName, cancellationToken);
    }

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default)
        => Inner.CreateContainerIfNotExistsAsync(cancellationToken);

    public Task<UploadResult> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
        => Inner.UploadAsync(blobName, content, metadata, tier, contentType, overwrite, cancellationToken);

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default)
        => Inner.OpenWriteAsync(blobName, contentType, cancellationToken);

    public Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => Inner.DownloadAsync(blobName, cancellationToken);

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => Inner.GetMetadataAsync(blobName, cancellationToken);

    public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, BlobListPrefixKind prefixKind = BlobListPrefixKind.DirectoryPrefix, bool includeMetadata = false, CancellationToken cancellationToken = default)
        => Inner.ListAsync(prefix, prefixKind, includeMetadata, cancellationToken);

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        => Inner.SetMetadataAsync(blobName, metadata, cancellationToken);

    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default)
        => Inner.SetTierAsync(blobName, tier, cancellationToken);

    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default)
        => Inner.CopyAsync(sourceBlobName, destinationBlobName, destinationTier, rehydratePriority, cancellationToken);

    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => Inner.DeleteAsync(blobName, cancellationToken);
}
