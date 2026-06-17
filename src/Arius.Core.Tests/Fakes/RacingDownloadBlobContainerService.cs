using Arius.Tests.Shared.Storage;

namespace Arius.Core.Tests.Fakes;

/// <summary>
/// Wraps a <see cref="FakeInMemoryBlobContainerService"/> and makes one blob "vanish" at download time even though
/// it is still returned by listings — modelling the race where a concurrent split deletes a shard between this
/// run's listing and its download. <see cref="TryDownloadAsync"/> returns <see langword="null"/> for
/// <see cref="_target"/> up to <see cref="MaxMisses"/> times (and runs <see cref="_onFirstMiss"/> on the first
/// miss, so a test can simulate the split landing); every other call is delegated to <see cref="Inner"/>.
/// </summary>
internal sealed class RacingDownloadBlobContainerService(
    FakeInMemoryBlobContainerService inner,
    RelativePath target,
    Action? onFirstMiss = null) : IBlobContainerService
{
    private readonly RelativePath _target      = target;
    private readonly Action?      _onFirstMiss = onFirstMiss;
    private          int          _missesServed;

    /// <summary>The shared underlying store; seed and inspect through this from the test.</summary>
    public FakeInMemoryBlobContainerService Inner { get; } = inner;

    /// <summary>How many times the target download returns null before it is delegated normally.</summary>
    public int MaxMisses { get; init; } = int.MaxValue;

    public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
    {
        if (blobName == _target)
        {
            var missNumber = Interlocked.Increment(ref _missesServed);
            if (missNumber <= MaxMisses)
            {
                if (missNumber == 1)
                    _onFirstMiss?.Invoke();
                return Task.FromResult<DownloadResult?>(null);
            }
        }

        return Inner.TryDownloadAsync(blobName, cancellationToken);
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
