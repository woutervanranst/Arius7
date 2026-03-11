using Arius.Azure;
using Testcontainers.Azurite;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Arius.Azure.Tests;

/// <summary>
/// Shared Azurite test fixture — starts one container per test class using it.
/// </summary>
public sealed class AzuriteFixture : IAsyncInitializer, IAsyncDisposable
{
    private AzuriteContainer _azurite = null!;
    private const string ContainerName = "arius-test";

    public async Task InitializeAsync()
    {
        _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
            .Build();

        await _azurite.StartAsync();

        // Create the blob container
        var client = new global::Azure.Storage.Blobs.BlobContainerClient(
            _azurite.GetConnectionString(), ContainerName);
        await client.CreateIfNotExistsAsync();
    }

    public string ConnectionString => _azurite.GetConnectionString();

    public AzureBlobStorageProvider CreateProvider()
    {
        var client = new global::Azure.Storage.Blobs.BlobContainerClient(
            _azurite.GetConnectionString(), ContainerName);
        return new AzureBlobStorageProvider(client);
    }

    public async ValueTask DisposeAsync()
    {
        await _azurite.DisposeAsync();
    }
}
