using Arius.Api.AppData;
using Arius.AzureBlob;
using Arius.Core;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Composition;

/// <summary>Production composer: opens the real Azure container and registers the real Arius.Core graph.
/// This is exactly the blob-open + registration that lived inline in <see cref="RepositoryProviderRegistry"/>.</summary>
public sealed class AzureRepositoryCoreComposer(IBlobServiceFactory blobServiceFactory) : IRepositoryCoreComposer
{
    public async Task ComposeAsync(IServiceCollection services, RepositoryConnection connection, PreflightMode mode, CancellationToken cancellationToken)
    {
        var blobService   = await blobServiceFactory.CreateAsync(connection.AccountName, connection.AccountKey, cancellationToken).ConfigureAwait(false);
        var blobContainer = await blobService.OpenContainerServiceAsync(connection.Container, mode, cancellationToken).ConfigureAwait(false);

        services.AddAzureBlobStorage();
        services.AddArius(blobContainer, connection.Passphrase, connection.AccountName, connection.Container);
    }
}
