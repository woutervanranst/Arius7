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
    private const string PreflightProbeBlobName = ".arius-preflight-probe";

    public async IAsyncEnumerable<string> GetContainerNamesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var container in serviceClient.GetBlobContainersAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var containerClient = serviceClient.GetBlobContainerClient(container.Name);
            var hasSnapshot = false;

            await foreach (var page in containerClient
                               .GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", BlobPaths.Snapshots, cancellationToken)
                               .AsPages(pageSizeHint: 1)
                               .ConfigureAwait(false))
            {
                if (page.Values.Any(item => item.IsBlob))
                {
                    hasSnapshot = true;
                    break;
                }
            }

            if (hasSnapshot)
            {
                yield return container.Name;
            }
        }
    }

    public async Task<IBlobContainerService> GetContainerServiceAsync(
        string containerName,
        PreflightMode preflightMode,
        CancellationToken cancellationToken = default)
    {
        var containerClient = serviceClient.GetBlobContainerClient(containerName);

        try
        {
            if (preflightMode == PreflightMode.ReadWrite)
            {
                var probeBlob = containerClient.GetBlobClient(PreflightProbeBlobName);
                await using var emptyStream = new MemoryStream();
                await probeBlob.UploadAsync(emptyStream, overwrite: true, cancellationToken).ConfigureAwait(false);
                await probeBlob.DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var exists = await containerClient.ExistsAsync(cancellationToken).ConfigureAwait(false);
                if (!exists.Value)
                {
                    throw new PreflightException(
                        PreflightErrorKind.ContainerNotFound,
                        authMode,
                        accountName,
                        containerName,
                        statusCode: 404);
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
