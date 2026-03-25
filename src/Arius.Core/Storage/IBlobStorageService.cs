namespace Arius.Core.Storage;

/// <summary>
/// Thrown by <see cref="IBlobStorageService.OpenWriteAsync"/> when
/// <c>throwOnExists = true</c> and the target blob already exists.
/// </summary>
public sealed class BlobAlreadyExistsException(string blobName)
    : Exception($"Blob '{blobName}' already exists in storage.");

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
/// Metadata returned by a HEAD or download operation on a blob.
/// </summary>
public sealed class BlobMetadata
{
    public required bool         Exists           { get; init; }
    public          BlobTier?    Tier             { get; init; }
    public          long?        ContentLength    { get; init; }
    public          bool         IsRehydrating    { get; init; }
    public          IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>
/// Abstracts all blob storage I/O. Arius.Core depends on this interface only —
/// no Azure-specific types cross this boundary.
/// </summary>
public interface IBlobStorageService
{
    // ── Upload ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads <paramref name="content"/> as a new blob at <paramref name="blobName"/>.
    /// <para>
    /// The caller is responsible for closing/disposing <paramref name="content"/> after the call.
    /// Set <paramref name="overwrite"/> to <c>true</c> to replace an existing blob.
    /// </para>
    /// </summary>
    Task UploadAsync(
        string                              blobName,
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
    /// When <paramref name="throwOnExists"/> is <c>true</c>, throws
    /// <see cref="BlobAlreadyExistsException"/> if the blob already exists, allowing the caller
    /// to inspect the existing blob and decide whether to skip or overwrite (crash recovery).
    /// When <c>false</c>, any existing blob is overwritten unconditionally.
    /// </para>
    /// </summary>
    Task<Stream> OpenWriteAsync(
        string            blobName,
        string?           contentType       = null,
        bool              throwOnExists     = false,
        CancellationToken cancellationToken = default);

    // ── Download ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a readable stream for the blob at <paramref name="blobName"/>.
    /// The caller must dispose the stream when done.
    /// </summary>
    Task<Stream> DownloadAsync(
        string            blobName,
        CancellationToken cancellationToken = default);

    // ── HEAD ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns existence, metadata, and tier information for <paramref name="blobName"/>.
    /// Does not download the blob body.
    /// </summary>
    Task<BlobMetadata> GetMetadataAsync(
        string            blobName,
        CancellationToken cancellationToken = default);

    // ── List ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all blob names that start with <paramref name="prefix"/>.
    /// </summary>
    IAsyncEnumerable<string> ListAsync(
        string            prefix,
        CancellationToken cancellationToken = default);

    // ── Metadata update ───────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the metadata of an existing blob without touching its body.
    /// </summary>
    Task SetMetadataAsync(
        string                              blobName,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken                   cancellationToken = default);

    /// <summary>
    /// Sets the access tier of an existing blob.
    /// </summary>
    Task SetTierAsync(
        string            blobName,
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
        string             sourceBlobName,
        string             destinationBlobName,
        BlobTier           destinationTier,
        RehydratePriority? rehydratePriority  = null,
        CancellationToken  cancellationToken  = default);

    // ── Delete ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes the blob at <paramref name="blobName"/>. No-ops if the blob does not exist.
    /// </summary>
    Task DeleteAsync(
        string            blobName,
        CancellationToken cancellationToken = default);
}
