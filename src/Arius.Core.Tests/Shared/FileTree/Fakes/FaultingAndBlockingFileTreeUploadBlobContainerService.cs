namespace Arius.Core.Tests.Shared.FileTree.Fakes;

/// <summary>
/// Blob-container fake that throws on the first filetree upload and blocks later filetree uploads
/// until cancellation. It exists to reproduce the producer/consumer deadlock path where an upload
/// worker faults after <see cref="FileTreeBuilder"/> has already started filling its bounded channel.
/// </summary>
internal sealed class FaultingAndBlockingFileTreeUploadBlobContainerService : IBlobContainerService
{
    private readonly TaskCompletionSource<bool> _blockedUploads = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _fileTreeUploads;

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<BlobMetadata> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        if (!blobName.StartsWith(BlobPaths.FileTreesPrefix))
            return new BlobMetadata { Exists = true, Tier = tier, Metadata = new Dictionary<string, string>(metadata) };

        if (Interlocked.Increment(ref _fileTreeUploads) == 1)
            throw new InvalidOperationException("Simulated filetree upload failure.");

        await _blockedUploads.Task.WaitAsync(cancellationToken);
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
}
