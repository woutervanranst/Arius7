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

        return new E2EStorageBackendContext
        {
            BlobContainer = service,
            AccountName = container.AccountName,
            ContainerName = container.Name,
            BlobContainerClient = container,
            AzureBlobContainerService = service,
            Capabilities = Capabilities,
            CleanupAsync = () => new ValueTask(container.DeleteIfExistsAsync(cancellationToken: cancellationToken)),
        };
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
