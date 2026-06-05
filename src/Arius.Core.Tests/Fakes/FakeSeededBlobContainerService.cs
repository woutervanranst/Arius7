namespace Arius.Core.Tests.Fakes;

/// <summary>
/// Read-only seeded blob store for tests that only need to list, HEAD, and download known blobs.
/// Used by list/query tests to model repository state without upload semantics.
/// Use this fake when a test needs seeded downloadable content plus list/exists behavior, but no
/// writes, metadata mutation, or conflict simulation.
/// </summary>
internal sealed class FakeSeededBlobContainerService : IBlobContainerService
{
    private readonly Dictionary<RelativePath, byte[]> _blobs = [];

    public void AddBlob(RelativePath blobName, byte[] content) => _blobs[blobName] = content;

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<UploadResult> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => await TryDownloadAsync(blobName, cancellationToken) ?? throw new BlobNotFoundException(blobName);

    public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
    {
        if (!_blobs.TryGetValue(blobName, out var content))
            return Task.FromResult<DownloadResult?>(null);

        return Task.FromResult<DownloadResult?>(new DownloadResult
        {
            Stream = new MemoryStream(content, writable: false),
            BlobIdentity = $"seeded:{blobName}",
        });
    }

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BlobMetadata { Exists = _blobs.ContainsKey(blobName) });

    public async IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata = false, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (includeMetadata)
            throw new NotSupportedException("FakeSeededBlobContainerService does not model metadata-aware listing.");

        foreach (var name in _blobs.Keys
                     .Where(name => name.StartsWith(prefix))
                     .OrderBy(name => name.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new BlobListItem { Name = name };
            await Task.Yield();
        }
    }
}
