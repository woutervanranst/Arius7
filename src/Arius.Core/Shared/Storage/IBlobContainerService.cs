namespace Arius.Core.Shared.Storage;

/// <summary>
/// Thrown when an upload is attempted against a blob that already exists,
/// and the caller used create-if-not-exists semantics (optimistic concurrency).
/// </summary>
public sealed class BlobAlreadyExistsException(RelativePath blobName) : IOException($"Blob already exists: {blobName}")
{
    public RelativePath BlobName { get; } = blobName;
}

/// <summary>
/// Thrown when a blob download is attempted against a blob that does not exist.
/// </summary>
public sealed class BlobNotFoundException(RelativePath blobName) : FileNotFoundException($"Blob not found: {blobName}")
{
    public RelativePath BlobName { get; } = blobName;
}

/// <summary>
/// Thrown when a blob download is attempted against a blob that is still in the archive tier
/// (not yet rehydrated). Lets callers re-route the chunk to the rehydration path.
/// </summary>
public sealed class BlobArchivedException(RelativePath blobName) : IOException($"Blob is archived: {blobName}")
{
    public RelativePath BlobName { get; } = blobName;
}

/// <summary>
/// Blob tier for uploaded content.
/// Maps to Azure access tiers; other backends may map these to equivalent concepts.
/// </summary>
public enum BlobTier
{
    Hot,
    Cool,
    Cold,
    Archive
}

/// <summary>
/// Rehydration priority for archive-tier blobs.
/// </summary>
public enum RehydratePriority
{
    Standard,
    High
}

/// <summary>
/// Result returned by a blob upload operation.
/// </summary>
public sealed record UploadResult
{
    public required string ETag { get; init; }
}

/// <summary>
/// Result returned by a blob download operation.
/// </summary>
public sealed record DownloadResult
{
    public required Stream Stream { get; init; }

    public required string ETag { get; init; }
}

/// <summary>
/// Metadata returned by a HEAD or download operation on a blob.
/// </summary>
public sealed record BlobMetadata
{
    public required bool                                Exists        { get; init; }
    public          string?                             ETag          { get; init; }
    public          BlobTier?                           Tier          { get; init; }
    public          long?                               ContentLength { get; init; }
    public          bool                                IsRehydrating { get; init; }
    public          IReadOnlyDictionary<string, string> Metadata      { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Blob information returned by prefix listings.
/// </summary>
public sealed record BlobListItem
{
    public required RelativePath Name { get; init; }

    public string? ETag { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public long? ContentLength { get; init; }

    public BlobTier? Tier { get; init; }
}

/// <summary>
/// Abstracts all blob storage I/O. Arius.Core depends on this interface only —
/// no Azure-specific types cross this boundary.
/// </summary>
public interface IBlobContainerService
{
    // ── Container ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures the blob container exists, creating it if it does not.
    /// Safe to call on every run — no-op if the container already exists.
    /// </summary>
    Task CreateContainerIfNotExistsAsync(CancellationToken cancellationToken = default);

    // ── Upload ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads <paramref name="content"/> as a new blob at <paramref name="blobName"/>.
    /// <para>
    /// The caller is responsible for closing/disposing <paramref name="content"/> after the call.
    /// Set <paramref name="overwrite"/> to <c>true</c> to replace an existing blob.
    /// When <paramref name="overwrite"/> is <c>false</c> and the blob already exists,
    /// throws <see cref="BlobAlreadyExistsException"/>.
    /// </para>
    /// </summary>
    Task<UploadResult> UploadAsync(
        RelativePath                        blobName,
        Stream                              content,
        IReadOnlyDictionary<string, string> metadata,
        BlobTier                            tier,
        string?                             contentType        = null,
        bool                                overwrite          = false,
        CancellationToken                   cancellationToken  = default);

    /// <summary>
    /// Opens a writable <see cref="Stream"/> for the blob at <paramref name="blobName"/>.
    /// Data written to the returned stream is uploaded directly to the backing store.
    /// The caller must dispose the stream to complete the upload.
    /// <para>
    /// Uses optimistic concurrency: if the blob already exists the stream open will throw
    /// <see cref="BlobAlreadyExistsException"/>. The caller is responsible for handling the
    /// conflict (recover if complete, delete and retry if partial).
    /// </para>
    /// </summary>
    Task<Stream> OpenWriteAsync(
        RelativePath      blobName,
        string?           contentType       = null,
        CancellationToken cancellationToken = default);

    // ── Download ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a readable stream for the blob at <paramref name="blobName"/> together with
    /// the current ETag.
    /// The caller must dispose <see cref="DownloadResult.Stream"/> when done.
    /// Throws <see cref="BlobNotFoundException"/> when the blob does not exist.
    /// </summary>
    Task<DownloadResult> DownloadAsync(
        RelativePath      blobName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a readable stream for the blob at <paramref name="blobName"/> together with the
    /// current ETag, or <c>null</c> when the blob does not exist.
    /// The caller must dispose <see cref="DownloadResult.Stream"/> when one is returned.
    /// </summary>
    Task<DownloadResult?> TryDownloadAsync(
        RelativePath      blobName,
        CancellationToken cancellationToken = default);

    // ── HEAD ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns existence, metadata, and tier information for <paramref name="blobName"/>.
    /// Does not download the blob body.
    /// </summary>
    Task<BlobMetadata> GetMetadataAsync(
        RelativePath      blobName,
        CancellationToken cancellationToken = default);

    // ── List ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all blobs that start with <paramref name="prefix"/>.
    /// Metadata is only populated when <paramref name="includeMetadata"/> is true.
    /// </summary>
    IAsyncEnumerable<BlobListItem> ListAsync(
        RelativePath      prefix,
        bool              includeMetadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists blobs directly under <paramref name="directory"/> whose final name segment starts
    /// with <paramref name="namePrefix"/> as a raw string prefix (NOT segment-aligned):
    /// <c>ListAsync(chunk-index, "aa")</c> matches <c>chunk-index/aa</c>, <c>chunk-index/aa0</c>
    /// and <c>chunk-index/aa3f</c>.
    /// The default implementation filters a directory listing client-side; backends that support
    /// native raw-prefix listing should override it.
    /// </summary>
    async IAsyncEnumerable<BlobListItem> ListAsync(
        RelativePath      directory,
        string            namePrefix,
        bool              includeMetadata   = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in ListAsync(directory, includeMetadata, cancellationToken))
        {
            if (item.Name.Name.ToString().StartsWith(namePrefix, StringComparison.Ordinal))
                yield return item;
        }
    }

    // ── Metadata update ───────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the metadata of an existing blob without touching its body.
    /// </summary>
    Task SetMetadataAsync(
        RelativePath                        blobName,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken                   cancellationToken = default);

    /// <summary>
    /// Sets the access tier of an existing blob.
    /// </summary>
    Task SetTierAsync(
        RelativePath      blobName,
        BlobTier          tier,
        CancellationToken cancellationToken = default);

    // ── Copy (rehydration) ────────────────────────────────────────────────────

    /// <summary>
    /// Initiates a server-side copy from <paramref name="sourceBlobName"/> to
    /// <paramref name="destinationBlobName"/> at <paramref name="destinationTier"/>.
    /// For archive-tier sources, pass <paramref name="rehydratePriority"/> to trigger rehydration.
    /// Returns immediately — the copy may still be in progress when this method returns.
    /// </summary>
    Task CopyAsync(
        RelativePath       sourceBlobName,
        RelativePath       destinationBlobName,
        BlobTier           destinationTier,
        RehydratePriority? rehydratePriority  = null,
        CancellationToken  cancellationToken  = default);

    // ── Delete ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes the blob at <paramref name="blobName"/>. No-ops if the blob does not exist.
    /// </summary>
    Task DeleteAsync(
        RelativePath      blobName,
        CancellationToken cancellationToken = default);
}
