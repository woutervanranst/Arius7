namespace Arius.E2E.Tests.Fixtures;

internal interface IE2EStorageBackend : IAsyncDisposable
{
    string Name { get; }

    E2EBackendCapabilities Capabilities { get; }

    Task InitializeAsync();

    Task<E2EStorageBackendContext> CreateContextAsync(CancellationToken cancellationToken = default);
}
