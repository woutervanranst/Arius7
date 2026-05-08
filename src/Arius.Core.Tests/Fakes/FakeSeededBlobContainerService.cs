using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Fakes;

/// <summary>
/// Read-only seeded blob store for tests that only need to list, HEAD, and download known blobs.
/// Used by list/query tests to model repository state without upload semantics.
/// Use this fake when a test needs seeded downloadable content plus list/exists behavior, but no
/// writes, metadata mutation, or conflict simulation.
/// </summary>
internal sealed class FakeSeededBlobContainerService : IBlobContainerService
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    public void AddBlob(RelativePath blobName, byte[] content) => _blobs[blobName.ToString()] = content;

    public void AddBlob(string blobName, byte[] content) => _blobs[blobName] = content;

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Stream> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default)
    {
        var blobKey = blobName.ToString();
        if (!_blobs.TryGetValue(blobKey, out var content))
            throw new FileNotFoundException(blobKey);

        return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
    }

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BlobMetadata { Exists = _blobs.ContainsKey(blobName.ToString()) });

    public async IAsyncEnumerable<RelativePath> ListAsync(RelativePath prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prefixText = prefix.ToString();
        foreach (var name in _blobs.Keys.Where(name => name.StartsWith(prefixText, StringComparison.Ordinal)).OrderBy(name => name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return RelativePath.Parse(name);
            await Task.Yield();
        }
    }
}
