using Arius.Core.Shared.Storage;

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

    public IAsyncEnumerable<RelativePath> ListAsync(RelativePath prefix, CancellationToken cancellationToken = default)
        => AsyncEnumerable();

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default)
        => Task.FromResult(blobName switch
        {
            var name when name == BlobPaths.ChunkRehydratedPath(FakeChunkHash('a')) => new BlobMetadata { Exists = true, ContentLength = 3 },
            var name when name == BlobPaths.ChunkRehydratedPath(FakeChunkHash('b')) => new BlobMetadata { Exists = true, ContentLength = 4 },
            _ => new BlobMetadata { Exists = false }
        });

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

    public Task<Stream> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task WaitForConcurrentDeletesAsync() => _sawConcurrentDeletes.Task;

    public void ReleaseDeletes() => _releaseDeletes.TrySetResult();

    private async IAsyncEnumerable<RelativePath> AsyncEnumerable()
    {
        yield return BlobPaths.ChunkRehydratedPath(FakeChunkHash('a'));
        yield return BlobPaths.ChunkRehydratedPath(FakeChunkHash('b'));
        await Task.CompletedTask;
    }
}
