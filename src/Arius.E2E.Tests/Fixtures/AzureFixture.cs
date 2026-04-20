using Arius.AzureBlob;
using Azure.Storage;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using TUnit.Core.Interfaces;

namespace Arius.E2E.Tests.Fixtures;

internal sealed class AzureFixture : AzureE2EBackendFixture
{
}

/// <summary>
/// Connects to a real Azure Storage account for E2E testing.
/// Credentials are read (in order) from environment variables or dotnet user-secrets:
///   ARIUS_E2E_ACCOUNT  — storage account name
///   ARIUS_E2E_KEY      — storage account key
///
/// To set via user-secrets:
///   dotnet user-secrets set "ARIUS_E2E_ACCOUNT" "..." --project src/Arius.E2E.Tests
///   dotnet user-secrets set "ARIUS_E2E_KEY"     "..." --project src/Arius.E2E.Tests
///
/// Each test run gets a unique container that is deleted on teardown.
/// Missing credentials leave the live Azure backend unavailable; tests that require it must skip explicitly.
/// </summary>
internal class AzureE2EBackendFixture : IE2EStorageBackend, IAsyncInitializer
{
    private static readonly Microsoft.Extensions.Configuration.IConfiguration _config = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .AddUserSecrets<AzureE2EBackendFixture>()
        .Build();

    public static readonly string? AccountName = _config["ARIUS_E2E_ACCOUNT"];
    public static readonly string? AccountKey  = _config["ARIUS_E2E_KEY"];

    /// <summary>True when both credentials are available.</summary>
    public static bool IsAvailable => !string.IsNullOrWhiteSpace(AccountName)
                                   && !string.IsNullOrWhiteSpace(AccountKey);

    private BlobServiceClient? _serviceClient;

    public string Name => "Azure";

    public E2EBackendCapabilities Capabilities { get; } = new(
        SupportsArchiveTier: true,
        SupportsRehydrationPlanning: true);

    public string Account    => AccountName ?? throw new InvalidOperationException("ARIUS_E2E_ACCOUNT not set.");
    public string Key        => AccountKey  ?? throw new InvalidOperationException("ARIUS_E2E_KEY not set.");

    public Task InitializeAsync()
    {
        if (!IsAvailable)
            return Task.CompletedTask;

        var credential   = new StorageSharedKeyCredential(Account, Key);
        var serviceUri   = new Uri($"https://{Account}.blob.core.windows.net");
        _serviceClient   = new BlobServiceClient(serviceUri, credential);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a unique container for a test run and returns the service backed by it.
    /// The container is automatically deleted when the returned disposable is disposed.
    /// </summary>
    public async Task<(BlobContainerClient Container, AzureBlobContainerService Service, Func<Task> Cleanup)>
        CreateTestContainerAsync(CancellationToken ct = default)
    {
        if (_serviceClient is null)
            throw new InvalidOperationException("AzureE2EBackendFixture not initialized.");

        var containerName = $"arius-e2e-{Guid.NewGuid():N}";
        var container     = _serviceClient.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: ct);

        var svc = new AzureBlobContainerService(container);

        async Task Cleanup()
        {
            try { await container.DeleteIfExistsAsync(cancellationToken: default); }
            catch { /* best-effort */ }
        }

        return (container, svc, Cleanup);
    }

    public async Task<E2EStorageBackendContext> CreateContextAsync(CancellationToken cancellationToken = default)
    {
        var (container, service, cleanup) = await CreateTestContainerAsync(cancellationToken);

        return new E2EStorageBackendContext
        {
            BlobContainer = service,
            AccountName = container.AccountName,
            ContainerName = container.Name,
            BlobContainerClient = container,
            AzureBlobContainerService = service,
            Capabilities = Capabilities,
            CleanupAsync = async () => await cleanup(),
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
