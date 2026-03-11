using Arius.Core.Models;

namespace Arius.Core.Infrastructure;

/// <summary>
/// Abstracts upload, download, list, delete, and tier-set operations over a blob store.
/// Lives in Arius.Core so that handlers can depend on it without referencing Azure SDK types.
/// </summary>
public interface IBlobStorageProvider
{
    Task UploadAsync(string blobName, Stream content, BlobTier tier, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string blobName, CancellationToken ct = default);
    IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct = default);
    Task DeleteAsync(string blobName, CancellationToken ct = default);
    Task SetTierAsync(string blobName, BlobTier tier, CancellationToken ct = default);
}
