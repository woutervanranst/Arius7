namespace Arius.Core.Storage;

/// <summary>
/// Thrown when an upload is attempted against a blob that already exists,
/// and the caller used create-if-not-exists semantics (optimistic concurrency).
/// </summary>
public sealed class BlobAlreadyExistsException : IOException
{
    public string BlobName { get; }

    public BlobAlreadyExistsException(string blobName)
        : base($"Blob already exists: {blobName}")
    {
        BlobName = blobName;
    }
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
    /// Uses optimistic concurrency: if the blob already exists the stream open will throw
    /// <see cref="BlobAlreadyExistsException"/>. The caller is responsible for handling the
    /// conflict (recover if complete, delete and retry if partial).
    /// </para>
    /// </summary>
    Task<Stream> OpenWriteAsync(
        string            blobName,
        string?           contentType       = null,
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
