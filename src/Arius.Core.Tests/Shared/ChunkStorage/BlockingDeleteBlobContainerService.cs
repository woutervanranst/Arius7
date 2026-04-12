using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Shared.ChunkStorage;

internal sealed class BlockingDeleteBlobContainerService : IBlobContainerService
{
    private readonly TaskCompletionSource _sawConcurrentDeletes = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseDeletes = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeDeletes;

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IAsyncEnumerable<string> ListAsync(string prefix = "", CancellationToken cancellationToken = default)
        => AsyncEnumerable();

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default)
        => Task.FromResult(blobName switch
        {
            "chunks-rehydrated/a" => new BlobMetadata { Exists = true, ContentLength = 3 },
            "chunks-rehydrated/b" => new BlobMetadata { Exists = true, ContentLength = 4 },
            _ => new BlobMetadata { Exists = false }
        });

    public async Task DeleteAsync(string blobName, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _activeDeletes) >= 2)
            _sawConcurrentDeletes.TrySetResult();

        try
        {
            await _releaseDeletes.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _activeDeletes);
        }
    }

    public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task WaitForConcurrentDeletesAsync() => _sawConcurrentDeletes.Task;

    public void ReleaseDeletes() => _releaseDeletes.TrySetResult();

    private async IAsyncEnumerable<string> AsyncEnumerable()
    {
        yield return BlobPaths.ChunkRehydrated("a");
        yield return BlobPaths.ChunkRehydrated("b");
        await Task.CompletedTask;
    }
}
