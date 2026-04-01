using Azure.Core;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Arius.Core.Storage;

namespace Arius.AzureBlob;

/// <summary>
/// Constructs a preflight-validated <see cref="IBlobStorageService"/> from caller-supplied
/// credentials.  This is a static factory — it has no instance state and runs before the
/// DI container is built.
/// </summary>
public static class BlobServiceFactory
{
    private const string PreflightProbeBlobName = ".arius-preflight-probe";

    /// <summary>
    /// Creates an Azure CLI token credential as an opaque <see cref="object"/>.
    /// Callers (e.g. the CLI host) use this so they do not need to reference
    /// <c>Azure.Identity</c> directly.
    /// </summary>
    public static object CreateAzureCliCredential() => new AzureCliCredential();

    /// <summary>
    /// Creates a shared-key credential as an opaque <see cref="object"/>.
    /// Callers (e.g. the CLI host) use this so they do not need to reference
    /// <c>Azure.Storage</c> directly.
    /// </summary>
    public static object CreateSharedKeyCredential(string accountName, string accountKey)
        => new StorageSharedKeyCredential(accountName, accountKey);

    /// <summary>
    /// Creates a <see cref="BlobServiceClient"/>, runs a preflight connectivity probe,
    /// and returns a validated <see cref="IBlobStorageService"/>.
    /// </summary>
    /// <param name="credential">
    /// Either a <see cref="StorageSharedKeyCredential"/> or a <see cref="TokenCredential"/>.
    /// Any other type throws <see cref="ArgumentException"/>.
    /// </param>
    /// <param name="accountName">Azure Storage account name.</param>
    /// <param name="containerName">Blob container name.</param>
    /// <param name="preflightMode">Controls the type of preflight probe executed.</param>
    /// <returns>A validated <see cref="IBlobStorageService"/>.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="credential"/> is not a supported type.</exception>
    /// <exception cref="PreflightException">Thrown if the preflight probe fails due to connectivity or auth errors.</exception>
    public static async Task<IBlobStorageService> CreateAsync(
        object        credential,
        string        accountName,
        string        containerName,
        PreflightMode preflightMode)
    {
        var serviceUri = new Uri($"https://{accountName}.blob.core.windows.net");

        // ── Credential type check and client construction ─────────────────────
        BlobServiceClient blobServiceClient;
        string authMode;

        switch (credential)
        {
            case StorageSharedKeyCredential keyCredential:
                blobServiceClient = new BlobServiceClient(serviceUri, keyCredential);
                authMode = "key";
                break;

            case TokenCredential tokenCredential:
                blobServiceClient = new BlobServiceClient(serviceUri, tokenCredential);
                authMode = "token";
                break;

            default:
                throw new ArgumentException(
                    $"Unsupported credential type '{credential?.GetType().FullName}'. " +
                    $"Must be {nameof(StorageSharedKeyCredential)} or {nameof(TokenCredential)}.",
                    nameof(credential));
        }

        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

        // ── Preflight probe ───────────────────────────────────────────────────
        try
        {
            if (preflightMode == PreflightMode.ReadWrite)
            {
                var probeBlob = containerClient.GetBlobClient(PreflightProbeBlobName);
                using var emptyStream = new MemoryStream();
                await probeBlob.UploadAsync(emptyStream, overwrite: true).ConfigureAwait(false);
                await probeBlob.DeleteAsync().ConfigureAwait(false);
            }
            else
            {
                // Use GetBlobsAsync (one-item page) rather than ExistsAsync so that
                // we exercise the ListBlobs permission, not just the container-metadata
                // permission.  ExistsAsync only requires Read on the container resource,
                // but listing blobs requires Storage Blob Data Reader — which is exactly
                // what restore/ls need in practice.
                var page = containerClient
                    .GetBlobsAsync()
                    .AsPages(pageSizeHint: 1);
                await foreach (var _ in page.ConfigureAwait(false))
                    break; // one page is enough; an empty container is fine too
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
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            throw new PreflightException(
                PreflightErrorKind.ContainerNotFound,
                authMode,
                accountName,
                containerName,
                statusCode: 404,
                inner: ex);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 403)
        {
            throw new PreflightException(
                PreflightErrorKind.AccessDenied,
                authMode,
                accountName,
                containerName,
                statusCode: 403,
                inner: ex);
        }
        catch (Azure.RequestFailedException ex)
        {
            throw new PreflightException(
                PreflightErrorKind.Other,
                authMode,
                accountName,
                containerName,
                statusCode: ex.Status,
                inner: ex);
        }
        catch (PreflightException)
        {
            throw; // don't re-wrap our own exceptions
        }

        // ── Return validated service ──────────────────────────────────────────
        return new AzureBlobStorageService(containerClient);
    }
}
