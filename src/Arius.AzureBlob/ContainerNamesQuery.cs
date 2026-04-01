using Azure.Storage.Blobs;
using Mediator;
using System.Runtime.CompilerServices;

namespace Arius.AzureBlob;

/// <summary>
/// Streams the names of Arius repository containers from an Azure storage account.
/// A container is considered an Arius repository if it contains at least one blob
/// under the <c>snapshots/</c> prefix.
/// </summary>
public sealed record ContainerNamesQuery(BlobServiceClient ServiceClient)
    : IStreamQuery<string>;

/// <summary>
/// Handler for <see cref="ContainerNamesQuery"/>.
/// Lists all containers and filters to those that contain a <c>snapshots/</c> blob.
/// </summary>
public sealed class ContainerNamesQueryHandler
    : IStreamQueryHandler<ContainerNamesQuery, string>
{
    private const string SnapshotsPrefix = "snapshots/";

    public async IAsyncEnumerable<string> Handle(
        ContainerNamesQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var container in query.ServiceClient
            .GetBlobContainersAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var containerClient = query.ServiceClient.GetBlobContainerClient(container.Name);

            // Check for at least one blob under snapshots/ (maxResults=1 equivalent via Take)
            var hasSnapshot = false;
            await foreach (var _ in containerClient
                .GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, SnapshotsPrefix, cancellationToken)
                .ConfigureAwait(false))
            {
                hasSnapshot = true;
                break;
            }

            if (hasSnapshot)
            {
                yield return container.Name;
            }
        }
    }
}
