using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Fakes;

/// <summary>
/// Read-only seeded blob store for tests that only need to list, HEAD, and download known blobs.
/// Used by list/query tests to model repository state without upload semantics.
/// </summary>
internal sealed class FakeSeededBlobContainerService : IBlobContainerService
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    public void AddBlob(string blobName, byte[] content) => _blobs[blobName] = content;

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default)
    {
        if (!_blobs.TryGetValue(blobName, out var content))
            throw new FileNotFoundException(blobName);

        return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
    }

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BlobMetadata { Exists = _blobs.ContainsKey(blobName) });

    public async IAsyncEnumerable<string> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var name in _blobs.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return name;
            await Task.Yield();
        }
    }
}
