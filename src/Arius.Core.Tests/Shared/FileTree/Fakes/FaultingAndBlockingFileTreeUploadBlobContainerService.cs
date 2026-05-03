using Arius.Core.Shared.Storage;

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

    public async Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        if (!blobName.StartsWith(BlobPaths.FileTrees, StringComparison.Ordinal))
            return;

        if (Interlocked.Increment(ref _fileTreeUploads) == 1)
            throw new InvalidOperationException("Simulated filetree upload failure.");

        await _blockedUploads.Task.WaitAsync(cancellationToken);
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
}
