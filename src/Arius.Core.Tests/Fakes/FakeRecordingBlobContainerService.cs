using System.Collections.Concurrent;

namespace Arius.Core.Tests.Fakes;

/// <summary>
/// Minimal recording fake for tree-builder tests. It tracks uploads and HEAD checks while keeping
/// all other blob operations inert so the tests can assert dedup/cache behavior without storage I/O.
/// Use this fake when a test only needs to observe which blobs were uploaded or checked, not to
/// persist or retrieve blob contents.
/// </summary>
internal sealed class FakeRecordingBlobContainerService : IBlobContainerService
{
    private readonly ConcurrentDictionary<RelativePath, byte> _remoteBlobs = new();

    public ConcurrentDictionary<RelativePath, byte> Uploaded { get; } = new();
    public ConcurrentDictionary<RelativePath, byte> HeadChecked { get; } = new();

    public void SeedRemoteBlob(RelativePath blobName) => _remoteBlobs.TryAdd(blobName, 0);

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<UploadResult> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        Uploaded.TryAdd(blobName, 0);
        return Task.FromResult(new UploadResult { ETag = $"recorded:{blobName}" });
    }

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream());

    public Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DownloadResult { Stream = new MemoryStream(), ETag = $"recorded:{blobName}" });

    public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult<DownloadResult?>(new DownloadResult { Stream = new MemoryStream(), ETag = $"recorded:{blobName}" });

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default)
    {
        HeadChecked.TryAdd(blobName, 0);
        return Task.FromResult(new BlobMetadata { Exists = _remoteBlobs.ContainsKey(blobName) });
    }

    public async IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata = false, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var blobName in _remoteBlobs.Keys
                     .Where(name => name.StartsWith(prefix))
                     .OrderBy(name => name.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new BlobListItem { Name = blobName };
            await Task.Yield();
        }
    }

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
