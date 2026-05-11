using Arius.Core.Shared.Storage;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
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

    public async Task<IBlobContainerService> GetContainerServiceAsync(
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
                await probeBlob.UploadAsync(emptyStream, overwrite: true, cancellationToken).ConfigureAwait(false);
                await probeBlob.DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Read-only callers need blob listing access, not just container metadata.
                var pages = containerClient
                    .GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix: default, cancellationToken)
                    .AsPages(pageSizeHint: 1);

                await foreach (var _ in pages.ConfigureAwait(false))
                {
                    break;
                }
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

        return new AzureBlobContainerService(containerClient);
    }
}
