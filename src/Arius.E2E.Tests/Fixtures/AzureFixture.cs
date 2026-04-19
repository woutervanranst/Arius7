using Arius.AzureBlob;
using Azure.Storage;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using TUnit.Core.Interfaces;

namespace Arius.E2E.Tests.Fixtures;

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
/// Missing credentials are treated as a test configuration error and fail the suite.
/// </summary>
public sealed class AzureFixture : IAsyncInitializer, IAsyncDisposable
{
    private static readonly Microsoft.Extensions.Configuration.IConfiguration _config = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .AddUserSecrets<AzureFixture>()
        .Build();

    public static readonly string? AccountName = _config["ARIUS_E2E_ACCOUNT"];
    public static readonly string? AccountKey  = _config["ARIUS_E2E_KEY"];

    /// <summary>True when both credentials are available.</summary>
    public static bool IsAvailable => !string.IsNullOrWhiteSpace(AccountName)
                                   && !string.IsNullOrWhiteSpace(AccountKey);

    private BlobServiceClient? _serviceClient;

    public string Account    => AccountName ?? throw new InvalidOperationException("ARIUS_E2E_ACCOUNT not set.");
    public string Key        => AccountKey  ?? throw new InvalidOperationException("ARIUS_E2E_KEY not set.");

    public Task InitializeAsync()
    {
        if (!IsAvailable)
            throw new InvalidOperationException(
                "ARIUS_E2E_ACCOUNT and ARIUS_E2E_KEY must be configured via environment variables or user secrets before running Arius.E2E.Tests.");

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
            throw new InvalidOperationException("AzureFixture not initialized.");

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

    public async ValueTask DisposeAsync()
    {
        // Service client has no resources to release; containers cleaned up per-test.
        await Task.CompletedTask;
    }
}
