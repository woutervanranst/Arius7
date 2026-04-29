using Arius.AzureBlob;
using Azure.Storage.Blobs;
using DotNet.Testcontainers.Builders;
using Testcontainers.Azurite;
using TUnit.Core.Interfaces;

namespace Arius.Tests.Shared.Fixtures;

/// <summary>
/// Shared TUnit session fixture that owns the Azurite Docker process and creates disposable test
/// containers/services on demand. Tests typically combine this shared backend fixture with a fresh
/// <see cref="RepositoryTestFixture"/> per test so repository state stays isolated while Azurite
/// startup cost is paid once per test session.
/// </summary>

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
        catch (Exception exception) when (IsUnsupportedAzuriteImage(exception))
        {
            _azurite = null;
            _unavailableReason = $"Azurite Docker image is unsupported in this environment: {exception.Message}";
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

    static bool IsUnsupportedAzuriteImage(Exception exception)
        => exception.Message.Contains("no matching manifest", StringComparison.OrdinalIgnoreCase)
            || (exception.GetType().Name == "DockerImageNotFoundException"
                && exception.Message.Contains("mcr.microsoft.com/azure-storage/azurite", StringComparison.OrdinalIgnoreCase));

    void EnsureAvailable()
    {
        if (IsAvailable)
            return;

        var reason = _unavailableReason ?? "Docker is unavailable for Azurite-backed tests.";
        Skip.Test(reason);
        throw new InvalidOperationException(reason);
    }
}
