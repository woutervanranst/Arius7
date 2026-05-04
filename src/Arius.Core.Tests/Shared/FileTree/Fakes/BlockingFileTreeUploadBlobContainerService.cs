using System.Collections.Concurrent;
using Arius.Core.Shared.Storage;

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

    public ConcurrentDictionary<string, byte> Uploaded { get; } = new(StringComparer.Ordinal);

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        if (blobName.StartsWith(BlobPaths.FileTrees, StringComparison.Ordinal))
        {
            if (Interlocked.Increment(ref _startedUploads) >= 2)
                _twoUploadsStarted.TrySetResult(true);

            await _allowUploads.Task.WaitAsync(cancellationToken);
        }

        Uploaded.TryAdd(blobName, 0);
    }

    public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream());

    public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream());

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BlobMetadata { Exists = false });

    public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<string>();

    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) => Task.CompletedTask;

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
