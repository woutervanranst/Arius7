using Arius.AzureBlob;
using Azure.Storage;
using Azure.Storage.Blobs;
using TUnit.Core.Interfaces;

namespace Arius.E2E.Tests;

/// <summary>
/// Connects to a real Azure Storage account for E2E testing.
/// Tests are skipped unless both environment variables are set:
///   ARIUS_E2E_ACCOUNT  — storage account name
///   ARIUS_E2E_KEY      — storage account key
///
/// Each test run gets a unique container that is deleted on teardown.
/// </summary>
public sealed class AzureFixture : IAsyncInitializer, IAsyncDisposable
{
    public static readonly string? AccountName = Environment.GetEnvironmentVariable("ARIUS_E2E_ACCOUNT");
    public static readonly string? AccountKey  = Environment.GetEnvironmentVariable("ARIUS_E2E_KEY");

    /// <summary>True when both env vars are set and E2E tests should run.</summary>
    public static bool IsAvailable => !string.IsNullOrWhiteSpace(AccountName)
                                   && !string.IsNullOrWhiteSpace(AccountKey);

    private BlobServiceClient? _serviceClient;

    public string Account    => AccountName ?? throw new InvalidOperationException("ARIUS_E2E_ACCOUNT not set.");
    public string Key        => AccountKey  ?? throw new InvalidOperationException("ARIUS_E2E_KEY not set.");

    public Task InitializeAsync()
    {
        if (!IsAvailable) return Task.CompletedTask;

        var credential   = new StorageSharedKeyCredential(Account, Key);
        var serviceUri   = new Uri($"https://{Account}.blob.core.windows.net");
        _serviceClient   = new BlobServiceClient(serviceUri, credential);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a unique container for a test run and returns the service backed by it.
    /// The container is automatically deleted when the returned disposable is disposed.
    /// </summary>
    public async Task<(BlobContainerClient Container, AzureBlobStorageService Service, Func<Task> Cleanup)>
        CreateTestContainerAsync(CancellationToken ct = default)
    {
        if (_serviceClient is null)
            throw new InvalidOperationException("AzureFixture not initialized.");

        var containerName = $"arius-e2e-{Guid.NewGuid():N}";
        var container     = _serviceClient.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: ct);

        var svc = new AzureBlobStorageService(container);

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
