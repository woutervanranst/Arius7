using Azure;
using Azure.Identity;
using Azure.Storage.Blobs.Models;

namespace Arius.AzureBlob;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IBlobService"/>.
/// Operates at the storage-account level via <see cref="BlobServiceClient"/>.
/// </summary>
public sealed class AzureBlobService(BlobServiceClient serviceClient, string accountName, string authMode) : IBlobService
{
    public async IAsyncEnumerable<string> GetContainerNamesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var container in serviceClient.GetBlobContainersAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var containerClient = serviceClient.GetBlobContainerClient(container.Name);

            if (await IsAriusArchive(containerClient, cancellationToken).ConfigureAwait(false))
                yield return container.Name;
        }

        static async Task<bool> IsAriusArchive(BlobContainerClient containerClient, CancellationToken cancellationToken)
        {
            var page = await containerClient
                .GetBlobsAsync(BlobTraits.None, BlobStates.None, BlobPaths.SnapshotsPrefix.ToBlobPrefix(), cancellationToken)
                .AsPages(pageSizeHint: 1)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            // We agree that a container is an Arius archive if it contains any blobs under the "snapshots/" prefix, excluding the edge case should a "snapshots/" blob exist without any actual snapshots.
            return page?.Values.Any(i => !i.Name.Equals(BlobPaths.SnapshotsPrefix.ToBlobPrefix(), StringComparison.Ordinal)) ?? false;
        }
    }

    /// <summary>
    /// Validates access to <paramref name="containerName"/> per <paramref name="preflightMode"/> and returns a
    /// container-scoped blob service.
    /// </summary>
    /// <remarks>
    /// This method is not a pure getter — it has side effects driven by <paramref name="preflightMode"/>:
    /// <list type="bullet">
    /// <item><see cref="PreflightMode.ReadWrite"/> (archive): probes write access by uploading and deleting a probe
    /// blob. If the upload reports the container is missing (first run), it <b>creates the container</b> instead — a
    /// credential able to create a container can also write blobs, so the create itself proves write access. The
    /// common case (container already exists) costs no extra round-trip, while first-run archives against a brand-new
    /// container still succeed instead of failing the preflight with <see cref="PreflightErrorKind.ContainerNotFound"/>.
    /// Container creation is idempotent and mirrors the archive handler's own <c>CreateContainerIfNotExistsAsync</c>.</item>
    /// <item><see cref="PreflightMode.ReadOnly"/> (restore, ls): requires the container to already exist and only
    /// probes list access; a missing container surfaces as <see cref="PreflightErrorKind.ContainerNotFound"/>.</item>
    /// </list>
    /// Throws <see cref="PreflightException"/> on credential/access/not-found failures — including a credential that
    /// lacks container-create permission, which surfaces as <see cref="PreflightErrorKind.AccessDenied"/> (HTTP 403).
    /// </remarks>
    public async Task<IBlobContainerService> OpenContainerServiceAsync(
        string containerName,
        PreflightMode preflightMode,
        CancellationToken cancellationToken = default)
    {
        const string preflightProbeBlobName = ".arius-preflight-probe";

        var containerClient = serviceClient.GetBlobContainerClient(containerName);

        try
        {
            if (preflightMode == PreflightMode.ReadWrite)
            {
                var probeBlob = containerClient.GetBlobClient(preflightProbeBlobName);
                await using var emptyStream = new MemoryStream();

                try
                {
                    // Hot path: the container already exists (every run after the first). The probe upload +
                    // delete validates write access without a redundant CreateIfNotExists round-trip per archive.
                    await probeBlob.UploadAsync(emptyStream, overwrite: true, cancellationToken).ConfigureAwait(false);
                    await probeBlob.DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // First-run archive: the container does not exist yet (uploading a blob into a missing
                    // container returns 404). Create it — idempotent with the archive handler's
                    // CreateContainerIfNotExistsAsync. No need to re-probe: every credential that can create a
                    // container can also write blobs (Shared Key, and the built-in Storage Blob Data
                    // Contributor/Owner roles bundle both), so a successful create already proves write access.
                    // A credential lacking create permission surfaces as a clean 403 ->
                    // PreflightException(AccessDenied) via the catch below.
                    await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                // Read-only callers need blob listing access, not just container metadata. Fetch a single
                // minimal page to probe list permission; a 404/403 surfaces via the catch clauses below.
                _ = await containerClient
                    .GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix: default, cancellationToken)
                    .AsPages(pageSizeHint: 1)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (CredentialUnavailableException ex)
        {
            throw new PreflightException(
                PreflightErrorKind.CredentialUnavailable,
                authMode,
                accountName,
                containerName,
                inner: ex);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new PreflightException(
                PreflightErrorKind.ContainerNotFound,
                authMode,
                accountName,
                containerName,
                statusCode: 404,
                inner: ex);
        }
        catch (RequestFailedException ex) when (ex.Status == 403)
        {
            throw new PreflightException(
                PreflightErrorKind.AccessDenied,
                authMode,
                accountName,
                containerName,
                statusCode: 403,
                inner: ex);
        }
        catch (RequestFailedException ex)
        {
            throw new PreflightException(
                PreflightErrorKind.Other,
                authMode,
                accountName,
                containerName,
                statusCode: ex.Status,
                inner: ex);
        }

        // Region lives in the container's own metadata (overridable in Azure Storage Explorer). Read it here,
        // and on a read/write open seed the "default" sentinel when absent so it is visible to edit. Best-effort:
        // metadata is used only to price cost estimates, so a failure here never fails the open.
        string? regionMetadata = null;
        try
        {
            var properties = await containerClient.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var metadata   = properties.Value.Metadata;
            metadata.TryGetValue(AzureBlobContainerService.RegionMetadataKey, out regionMetadata);

            if (preflightMode == PreflightMode.ReadWrite && string.IsNullOrEmpty(regionMetadata))
            {
                var seeded = new Dictionary<string, string>(metadata)
                {
                    [AzureBlobContainerService.RegionMetadataKey] = AzureBlobContainerService.UnsetRegionSentinel,
                };
                await containerClient.SetMetadataAsync(seeded, cancellationToken: cancellationToken).ConfigureAwait(false);
                regionMetadata = AzureBlobContainerService.UnsetRegionSentinel;
            }
        }
        catch (RequestFailedException)
        {
            // Ignore — region metadata is a best-effort pricing hint, not required to open the container.
        }

        return new AzureBlobContainerService(containerClient, regionMetadata);
    }
}
