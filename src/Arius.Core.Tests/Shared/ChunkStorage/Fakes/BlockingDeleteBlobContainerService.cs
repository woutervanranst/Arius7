namespace Arius.Core.Tests.Shared.ChunkStorage.Fakes;

/// <summary>
/// Holds rehydrated-chunk deletions open until the test releases them so cleanup tests can prove
/// the delete loop runs work concurrently instead of serializing each blob delete.
/// </summary>
internal sealed class BlockingDeleteBlobContainerService : IBlobContainerService
{
    private readonly TaskCompletionSource _sawConcurrentDeletes = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseDeletes = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeDeletes;

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata, CancellationToken cancellationToken = default)
        => AsyncEnumerable(prefix, cancellationToken);

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default)
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

    public Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UploadResult> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task WaitForConcurrentDeletesAsync() => _sawConcurrentDeletes.Task;

    public void ReleaseDeletes() => _releaseDeletes.TrySetResult();

    private async IAsyncEnumerable<BlobListItem> AsyncEnumerable(RelativePath prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var name in new[]
                 {
                     BlobPaths.ChunkRehydratedPath(FakeChunkHash('a')),
                     BlobPaths.ChunkRehydratedPath(FakeChunkHash('b')),
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name.StartsWith(prefix))
                yield return new BlobListItem { Name = name };
        }

        await Task.CompletedTask;
    }
}
