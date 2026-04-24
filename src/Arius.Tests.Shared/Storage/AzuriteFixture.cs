using Arius.AzureBlob;
using Azure.Storage.Blobs;
using DotNet.Testcontainers.Builders;
using Testcontainers.Azurite;
using TUnit.Core.Interfaces;

namespace Arius.Tests.Shared.Storage;

public sealed class AzuriteFixture : IAsyncInitializer, IAsyncDisposable
{
    private readonly Func<Task<AzuriteContainer>> _startAzuriteAsync;
    private AzuriteContainer? _azurite;
    private string? _unavailableReason;

    public AzuriteFixture()
        : this(StartAzuriteAsync)
    {
    }

    internal AzuriteFixture(Func<Task<AzuriteContainer>> startAzuriteAsync)
    {
        _startAzuriteAsync = startAzuriteAsync;
    }

    public bool IsAvailable => _azurite is not null;

    public string ConnectionString
    {
        get
        {
            EnsureAvailable();
            return _azurite!.GetConnectionString();
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            _azurite = await _startAzuriteAsync();
            _unavailableReason = null;
        }
        catch (DockerUnavailableException exception)
        {
            _azurite = null;
            _unavailableReason = $"Docker is unavailable for Azurite-backed tests: {exception.Message}";
        }
    }

    public async Task<(BlobContainerClient Container, AzureBlobContainerService Service)>
        CreateTestServiceAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailable();

        var containerName = $"test-{Guid.NewGuid():N}";
        var client = new BlobServiceClient(ConnectionString)
            .GetBlobContainerClient(containerName);
        await client.CreateAsync(cancellationToken: cancellationToken);
        return (client, new AzureBlobContainerService(client));
    }

    public AzureBlobContainerService CreateTestServiceFromExistingContainer(BlobContainerClient container)
    {
        EnsureAvailable();
        return new(container);
    }

    public async ValueTask DisposeAsync()
    {
        if (_azurite is not null)
            await _azurite.DisposeAsync();
    }

    static async Task<AzuriteContainer> StartAzuriteAsync()
    {
        var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
            .WithCommand("--skipApiVersionCheck")
            .Build();

        await azurite.StartAsync();
        return azurite;
    }

    void EnsureAvailable()
    {
        if (IsAvailable)
            return;

        var reason = _unavailableReason ?? "Docker is unavailable for Azurite-backed tests.";
        Skip.Test(reason);
        throw new InvalidOperationException(reason);
    }
}
