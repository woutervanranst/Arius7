namespace Arius.Core.Tests.Fakes;

/// <summary>
/// Lightweight metadata-focused <see cref="IBlobContainerService"/> fake used by list and
/// hydration-status tests. It only models HEAD-style lookups and tracks which blob names were
/// requested, keeping those tests explicit without carrying full upload/download behavior.
/// Use this fake when a test only calls <c>GetMetadataAsync</c> and should fail fast if it starts
/// depending on downloads, uploads, or other storage operations.
/// </summary>
internal sealed class FakeMetadataOnlyBlobContainerService : IBlobContainerService
{
    public Dictionary<RelativePath, BlobMetadata> Metadata { get; } = [];
    public List<RelativePath> RequestedBlobNames { get; } = [];

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<UploadResult> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default)
    {
        RequestedBlobNames.Add(blobName);
        return Task.FromResult(Metadata.TryGetValue(blobName, out var metadata) ? metadata : new BlobMetadata { Exists = false });
    }

    public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, BlobListPrefixKind prefixKind = BlobListPrefixKind.DirectoryPrefix, bool includeMetadata = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("FakeMetadataOnlyBlobContainerService is HEAD-only and does not support blob listing.");
}
