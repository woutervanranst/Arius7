using System.Collections.Concurrent;

namespace Arius.Core.Tests.Shared.FileTree.Fakes;

/// <summary>
/// Blob-container fake that blocks filetree uploads until the test releases them.
/// It exists to prove that <see cref="FileTreeBuilder"/> keeps building sibling trees while
/// upload workers are back-pressured, and to make concurrent upload starts observable without sleeps.
/// </summary>
internal sealed class BlockingFileTreeUploadBlobContainerService : IBlobContainerService
{
    private readonly TaskCompletionSource<bool> _allowUploads = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _twoUploadsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _startedUploads;

    public ConcurrentDictionary<RelativePath, byte> Uploaded { get; } = [];

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<BlobMetadata> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        if (blobName.StartsWith(BlobPaths.FileTreesPrefix))
        {
            if (Interlocked.Increment(ref _startedUploads) >= 2)
                _twoUploadsStarted.TrySetResult(true);

            await _allowUploads.Task.WaitAsync(cancellationToken);
        }

        Uploaded.TryAdd(blobName, 0);
        return new BlobMetadata { Exists = true, Tier = tier, Metadata = new Dictionary<string, string>(metadata) };
    }

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream());

    public Task<Stream> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream());

    public Task<Stream?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(new MemoryStream());

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BlobMetadata { Exists = false });

    public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata = false, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<BlobListItem>();

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<bool> WaitForTwoUploadsAsync(TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await _twoUploadsStarted.Task.WaitAsync(timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void AllowUploads() => _allowUploads.TrySetResult(true);
}
