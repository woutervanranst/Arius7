using System.Runtime.CompilerServices;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Core.Features.ContainerNamesQuery;

public sealed record ContainerNamesQuery(string AccountName, string? AccountKey) : IStreamQuery<string>;

public sealed class ContainerNamesQueryHandler(IServiceProvider serviceProvider)
    : IStreamQueryHandler<ContainerNamesQuery, string>
{
    public async IAsyncEnumerable<string> Handle(
        ContainerNamesQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var blobServiceFactory = serviceProvider.GetRequiredService<IBlobServiceFactory>();
        var blobService = await blobServiceFactory.CreateAsync(query.AccountName, query.AccountKey, cancellationToken)
            .ConfigureAwait(false);

        await foreach (var containerName in blobService.GetContainerNamesAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return containerName;
        }
    }
}
