using Arius.AzureBlob;
using Azure.Storage.Blobs;
using Testcontainers.Azurite;
using TUnit.Core.Interfaces;

namespace Arius.Tests.Shared.Storage;

public sealed class AzuriteFixture : IAsyncInitializer, IAsyncDisposable
{
    private AzuriteContainer? _azurite;

    public string ConnectionString => _azurite?.GetConnectionString()
        ?? throw new InvalidOperationException("Azurite not yet started.");

    public async Task InitializeAsync()
    {
        _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
            .WithCommand("--skipApiVersionCheck")
            .Build();
        await _azurite.StartAsync();
    }

    public async Task<(BlobContainerClient Container, AzureBlobContainerService Service)>
        CreateTestServiceAsync(CancellationToken cancellationToken = default)
    {
        var containerName = $"test-{Guid.NewGuid():N}";
        var client = new BlobServiceClient(ConnectionString)
            .GetBlobContainerClient(containerName);
        await client.CreateAsync(cancellationToken: cancellationToken);
        return (client, new AzureBlobContainerService(client));
    }

    public AzureBlobContainerService CreateTestServiceFromExistingContainer(BlobContainerClient container)
        => new(container);

    public async ValueTask DisposeAsync()
    {
        if (_azurite is not null)
            await _azurite.DisposeAsync();
    }
}
