using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Tests.Shared.FileTree.Fakes;

/// <summary>
/// Blob-container fake that returns snapshot names in the caller-provided order and records whether
/// filetree listing was requested.
/// It exists to prove <see cref="FileTreeService.ValidateAsync"/> sorts remote snapshots itself
/// instead of trusting backend enumeration order, and to distinguish fast-path validation from a
/// slow-path filetree scan.
/// </summary>
internal sealed class UnsortedSnapshotBlobContainerService(IReadOnlyList<string> snapshots) : IBlobContainerService
{
    public bool FileTreesListed { get; private set; }

    public Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UploadAsync(RelativePath blobName, Stream content, IReadOnlyDictionary<string, string> metadata, BlobTier tier, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Stream> OpenWriteAsync(RelativePath blobName, string? contentType = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Stream> DownloadAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<BlobMetadata> GetMetadataAsync(RelativePath blobName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BlobMetadata { Exists = false });

    public async IAsyncEnumerable<RelativePath> ListAsync(RelativePath prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (prefix == BlobPaths.SnapshotsPrefix)
        {
            foreach (var snapshot in snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return RelativePath.Parse(snapshot);
                await Task.Yield();
            }

            yield break;
        }

        if (prefix == BlobPaths.FileTreesPrefix)
            FileTreesListed = true;

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
