using Arius.AzureBlob;
using Arius.Core.Shared.Storage;
using Azure.Storage.Blobs;

namespace Arius.E2E.Tests.Fixtures;

internal sealed class E2EStorageBackendContext : IAsyncDisposable
{
    public required IBlobContainerService BlobContainer { get; init; }

    public required string AccountName { get; init; }

    public required string ContainerName { get; init; }

    public BlobContainerClient? BlobContainerClient { get; init; }

    public AzureBlobContainerService? AzureBlobContainerService { get; init; }

    public required E2EBackendCapabilities Capabilities { get; init; }

    public required Func<ValueTask> CleanupAsync { get; init; }

    public ValueTask DisposeAsync() => CleanupAsync();
}
