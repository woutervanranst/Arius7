namespace Arius.Core.Tests.Fakes;

internal sealed class ThrowOnCreateBlobContainerService(string operationName) : IBlobContainerService
{
    public bool CreateCalled { get; private set; }

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default)
    {
        CreateCalled = true;
        throw new InvalidOperationException($"CreateContainerIfNotExistsAsync should not be called by {operationName}.");
    }

    public Task<UploadResult> UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<DownloadResult> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<DownloadResult?> TryDownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BlobMetadata { Exists = false });

    public async IAsyncEnumerable<BlobListItem> ListAsync(RelativePath prefix, bool includeMetadata = false, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield break;
    }

    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
