using Azure.Core;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using Arius.Core.Storage;

namespace Arius.AzureBlob;

/// <summary>
/// Creates account-scoped blob services from caller-supplied credentials.
/// </summary>
public sealed class BlobServiceFactory : IBlobServiceFactory
{
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
