namespace Arius.Core.Tests.Fakes;

/// <summary>
/// Minimal recording fake for tree-builder tests. It tracks uploads and HEAD checks while keeping
/// all other blob operations inert so the tests can assert dedup/cache behavior without storage I/O.
/// Use this fake when a test only needs to observe which blobs were uploaded or checked, not to
/// persist or retrieve blob contents.
/// </summary>
internal sealed class FakeRecordingBlobContainerService : IBlobContainerService
{
    private readonly HashSet<RelativePath> _remoteBlobs = [];

    public HashSet<RelativePath> Uploaded { get; } = [];
    public HashSet<RelativePath> HeadChecked { get; } = [];

    public void SeedRemoteBlob(RelativePath blobName) => _remoteBlobs.Add(blobName);

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        Uploaded.Add(blobName);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream());

    public Task<Stream> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream());

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default)
    {
        HeadChecked.Add(blobName);
        return Task.FromResult(new BlobMetadata { Exists = _remoteBlobs.Contains(blobName) });
    }

    public async IAsyncEnumerable<RelativePath> ListAsync(RelativePath prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var blobName in _remoteBlobs
                     .Where(name => name.StartsWith(prefix))
                     .OrderBy(name => name.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return blobName;
            await Task.Yield();
        }
    }

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
