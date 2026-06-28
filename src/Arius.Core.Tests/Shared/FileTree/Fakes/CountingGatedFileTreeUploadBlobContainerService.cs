using System.Collections.Concurrent;

namespace Arius.Core.Tests.Shared.FileTree.Fakes;

/// <summary>
/// Blob-container fake that (1) counts how many times each blob is uploaded and (2) holds every
/// filetree upload at a gate until the test releases it.
///
/// The gate keeps all racing same-hash writers in flight simultaneously: if the store path lacks
/// per-hash coalescing, several workers handed identical sibling-subtree nodes (same content hash →
/// same blob name) all reach <see cref="UploadAsync"/> while gated, surfacing deterministically as a
/// second upload of the same blob name via <see cref="WaitForDuplicateFileTreeUploadAsync"/>. With
/// coalescing, only one worker uploads each hash and the others await the shared store, so no blob is
/// ever uploaded twice.
/// </summary>
internal sealed class CountingGatedFileTreeUploadBlobContainerService : IBlobContainerService
{
    private readonly TaskCompletionSource              _gate            = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RelativePath> _duplicateUpload = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentDictionary<RelativePath, int> UploadCounts { get; } = new();

    public void ReleaseUploads() => _gate.TrySetResult();

    /// <summary>Resolves to true if the same filetree blob is uploaded a second time before the timeout.</summary>
    public async Task<bool> WaitForDuplicateFileTreeUploadAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _duplicateUpload.Task.WaitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<UploadResult> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        var count = UploadCounts.AddOrUpdate(blobName, 1, static (_, c) => c + 1);

        if (blobName.StartsWith(BlobPaths.FileTreesPrefix))
        {
            if (count >= 2)
                _duplicateUpload.TrySetResult(blobName);

            await _gate.Task.WaitAsync(cancellationToken);
        }

        return new UploadResult { ETag = $"counting:{blobName}" };
    }

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream());

    public Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DownloadResult { Stream = new MemoryStream(), ETag = $"counting:{blobName}" });

    public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult<DownloadResult?>(new DownloadResult { Stream = new MemoryStream(), ETag = $"counting:{blobName}" });

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BlobMetadata { Exists = false });

    public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, BlobListPrefixKind prefixKind = BlobListPrefixKind.DirectoryPrefix, bool includeMetadata = false, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<BlobListItem>();

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
