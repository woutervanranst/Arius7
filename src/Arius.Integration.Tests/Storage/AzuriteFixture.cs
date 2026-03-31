using Arius.AzureBlob;
using Azure.Storage.Blobs;
using Testcontainers.Azurite;
using TUnit.Core.Interfaces;

namespace Arius.Integration.Tests.Storage;

/// <summary>
/// Manages a shared Azurite container for the entire integration test session.
/// Each test gets its own uniquely-named blob container to guarantee isolation.
///
/// Usage in a test class:
/// <code>
///   [ClassDataSource&lt;AzuriteFixture&gt;(Shared = SharedType.PerTestSession)]
///   public class MyTest(AzuriteFixture azurite) { ... }
/// </code>
/// </summary>
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

    /// <summary>
    /// Creates a new, uniquely-named blob container and returns
    /// an <see cref="AzureBlobStorageService"/> backed by that container.
    /// </summary>
    public async Task<(BlobContainerClient Container, AzureBlobStorageService Service)>
        CreateTestServiceAsync(CancellationToken cancellationToken = default)
    {
        var containerName = $"test-{Guid.NewGuid():N}";
        var client = new BlobServiceClient(ConnectionString)
            .GetBlobContainerClient(containerName);
        await client.CreateAsync(cancellationToken: cancellationToken);
        return (client, new AzureBlobStorageService(client));
    }

    /// <summary>
    /// Returns an <see cref="AzureBlobStorageService"/> backed by an existing container.
    /// Used to attach a second fixture to an already-populated container (e.g. mixed-archive test).
    /// </summary>
    public AzureBlobStorageService CreateTestServiceFromExistingContainer(BlobContainerClient container)
        => new(container);

    public async ValueTask DisposeAsync()
    {
        if (_azurite is not null)
            await _azurite.DisposeAsync();
    }
}
