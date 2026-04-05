using Arius.Core.Shared.Storage;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;

namespace Arius.AzureBlob;

/// <summary>
/// Creates account-scoped blob services from an account name and optional shared key.
/// </summary>
public sealed class AzureBlobServiceFactory : IBlobServiceFactory
{
    /// <summary>
    /// Creates an <see cref="IBlobService"/> for <paramref name="accountName"/>.
    /// Uses the shared key when provided; otherwise falls back to Azure CLI authentication.
    /// </summary>
    public Task<IBlobService> CreateAsync(
        string accountName,
        string? accountKey,
        CancellationToken cancellationToken = default)
    {
        var serviceUri = new Uri($"https://{accountName}.blob.core.windows.net");

        BlobServiceClient blobServiceClient;
        string authMode;

        if (!string.IsNullOrWhiteSpace(accountKey))
        {
            blobServiceClient = new BlobServiceClient(serviceUri, new StorageSharedKeyCredential(accountName, accountKey));
            authMode = "key";
        }
        else
        {
            blobServiceClient = new BlobServiceClient(serviceUri, new AzureCliCredential());
            authMode = "token";
        }

        return Task.FromResult<IBlobService>(new AzureBlobService(blobServiceClient, accountName, authMode));
    }
}
