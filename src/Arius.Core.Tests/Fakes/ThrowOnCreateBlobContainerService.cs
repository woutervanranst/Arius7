using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Fakes;

internal sealed class ThrowOnCreateBlobContainerService(string operationName) : IBlobContainerService
{
    public bool CreateCalled { get; private set; }

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default)
    {
        CreateCalled = true;
        throw new InvalidOperationException($"CreateContainerIfNotExistsAsync should not be called by {operationName}.");
    }

    public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BlobMetadata { Exists = false });

    public async IAsyncEnumerable<string> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield break;
    }

    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
