using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Fakes;

/// <summary>
/// Lightweight metadata-focused <see cref="IBlobContainerService"/> fake used by list and
/// hydration-status tests. It only models HEAD-style lookups and tracks which blob names were
/// requested, keeping those tests explicit without carrying full upload/download behavior.
/// Use this fake when a test only calls <c>GetMetadataAsync</c> and should fail fast if it starts
/// depending on downloads, uploads, or other storage operations.
/// </summary>
internal sealed class FakeMetadataOnlyBlobContainerService : IBlobContainerService
{
    public Dictionary<string, BlobMetadata> Metadata { get; } = new(StringComparer.Ordinal);
    public List<string> RequestedBlobNames { get; } = [];

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<Stream> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetMetadataAsync(RelativePath blobName, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetTierAsync(RelativePath blobName, BlobTier tier, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CopyAsync(RelativePath sourceBlobName, RelativePath destinationBlobName, BlobTier destinationTier, RehydratePriority? rehydratePriority = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(RelativePath blobName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default)
    {
        var blobKey = blobName.ToString();
        RequestedBlobNames.Add(blobKey);
        return Task.FromResult(Metadata.TryGetValue(blobKey, out var metadata) ? metadata : new BlobMetadata { Exists = false });
    }

    public async IAsyncEnumerable<RelativePath> ListAsync(RelativePath prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield break;
    }
}
