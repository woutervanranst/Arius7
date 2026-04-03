using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Fakes;

/// <summary>
/// Minimal recording fake for tree-builder tests. It tracks uploads and HEAD checks while keeping
/// all other blob operations inert so the tests can assert dedup/cache behavior without storage I/O.
/// Use this fake when a test only needs to observe which blobs were uploaded or checked, not to
/// persist or retrieve blob contents.
/// </summary>
internal sealed class FakeRecordingBlobContainerService : IBlobContainerService
{
    public HashSet<string> Uploaded { get; } = new(StringComparer.Ordinal);
    public HashSet<string> HeadChecked { get; } = new(StringComparer.Ordinal);

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UploadAsync(string blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        Uploaded.Add(blobName);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenWriteAsync(string blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream());

    public Task<Stream> DownloadAsync(string blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream());

    public Task<BlobMetadata> GetMetadataAsync(string blobName, CancellationToken cancellationToken = default)
    {
        HeadChecked.Add(blobName);
        return Task.FromResult(new BlobMetadata { Exists = false });
    }

    public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<string>();

    public Task SetMetadataAsync(string blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SetTierAsync(string blobName, BlobTier tier, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CopyAsync(string sourceBlobName, string destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteAsync(string blobName, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
