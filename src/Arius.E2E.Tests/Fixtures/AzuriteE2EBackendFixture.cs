using Arius.Integration.Tests.Storage;
using TUnit.Core.Interfaces;

namespace Arius.E2E.Tests.Fixtures;

internal sealed class AzuriteE2EBackendFixture : IE2EStorageBackend, IAsyncInitializer
{
    private readonly AzuriteFixture _inner = new();

    public string Name => "Azurite";

    public E2EBackendCapabilities Capabilities { get; } = new(
        SupportsArchiveTier: false,
        SupportsRehydrationPlanning: false);

    public Task InitializeAsync() => _inner.InitializeAsync();

    public async Task<E2EStorageBackendContext> CreateContextAsync(CancellationToken cancellationToken = default)
    {
        var (container, service) = await _inner.CreateTestServiceAsync(cancellationToken);

        async ValueTask CleanupAsync()
        {
            try
            {
                await container.DeleteIfExistsAsync(cancellationToken: default);
            }
            catch
            {
                // Best-effort cleanup; disposal should not fail the test path.
            }
        }

        return new E2EStorageBackendContext
        {
            BlobContainer = service,
            AccountName = container.AccountName,
            ContainerName = container.Name,
            BlobContainerClient = container,
            AzureBlobContainerService = service,
            Capabilities = Capabilities,
            CleanupAsync = CleanupAsync,
        };
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
