namespace Arius.Core.Application.Abstractions;

/// <summary>
/// Access tier for Azure Blob Storage.
/// Mirrors Azure.Storage.Blobs.Models.AccessTier without taking a dependency on the Azure SDK
/// from Arius.Core.
/// </summary>
public enum BlobAccessTier
{
    Hot,
    Cool,
    Cold,
    Archive
}

/// <summary>
/// Metadata returned when listing blobs.
/// </summary>
public sealed record BlobItem(string Name, long? ContentLength, DateTimeOffset? LastModified);

/// <summary>
/// Abstraction over Azure Blob Storage (or any blob store).
/// All methods operate on a single container; blob names are relative paths within that container.
/// </summary>
public interface IBlobStorageProvider
{
    // ── Upload ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads <paramref name="data"/> to <paramref name="blobName"/> with the specified
    /// <paramref name="tier"/>.  The caller is responsible for choosing the tier; no implicit
    /// tier selection is applied by the provider.
    /// </summary>
    ValueTask UploadAsync(
        string blobName,
        Stream data,
        BlobAccessTier tier,
        CancellationToken cancellationToken = default);

    // ── Download ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a non-seekable stream for reading <paramref name="blobName"/>.
    /// The caller must dispose the stream.
    /// Throws if the blob does not exist or is in Archive tier and has not been rehydrated.
    /// </summary>
    ValueTask<Stream> DownloadAsync(
        string blobName,
        CancellationToken cancellationToken = default);

    // ── Metadata ─────────────────────────────────────────────────────────────

    /// <summary>Returns true if the blob exists; false otherwise.</summary>
    ValueTask<bool> ExistsAsync(
        string blobName,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the current access tier of the blob.</summary>
    ValueTask<BlobAccessTier?> GetTierAsync(
        string blobName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the rehydration status string for an archive-tier blob that is being rehydrated
    /// (e.g. "rehydrate-pending-to-hot"), or null if the blob is not being rehydrated.
    /// </summary>
    ValueTask<string?> GetArchiveStatusAsync(
        string blobName,
        CancellationToken cancellationToken = default);

    // ── Tiering ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Initiates a tier change on <paramref name="blobName"/>.
    /// For Archive → Hot, this triggers rehydration (asynchronous on Azure).
    /// </summary>
    ValueTask SetTierAsync(
        string blobName,
        BlobAccessTier tier,
        CancellationToken cancellationToken = default);

    // ── Listing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all blobs with names beginning with <paramref name="prefix"/>,
    /// using continuation tokens internally.
    /// </summary>
    IAsyncEnumerable<BlobItem> ListAsync(
        string prefix,
        CancellationToken cancellationToken = default);

    // ── Deletion ─────────────────────────────────────────────────────────────

    /// <summary>Deletes the blob.  Does not throw if it does not exist.</summary>
    ValueTask DeleteAsync(
        string blobName,
        CancellationToken cancellationToken = default);

    // ── Lease (concurrency control) ───────────────────────────────────────────

    /// <summary>
    /// Acquires a 60-second lease on <paramref name="blobName"/>.
    /// Returns the lease ID to be used with renew/release calls.
    /// Throws if the blob is already leased.
    /// </summary>
    ValueTask<string> AcquireLeaseAsync(
        string blobName,
        CancellationToken cancellationToken = default);

    /// <summary>Renews a previously acquired lease identified by <paramref name="leaseId"/>.</summary>
    ValueTask RenewLeaseAsync(
        string blobName,
        string leaseId,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a previously acquired lease identified by <paramref name="leaseId"/>.</summary>
    ValueTask ReleaseLeaseAsync(
        string blobName,
        string leaseId,
        CancellationToken cancellationToken = default);
}
