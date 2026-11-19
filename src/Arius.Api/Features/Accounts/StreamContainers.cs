using System.Runtime.CompilerServices;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;
using Arius.Core.Features.ContainerNamesQuery;
using Mediator;

namespace Arius.Api.Features.Accounts;

/// <summary>
/// Streams the names of the containers in a storage account. Pass a positive <c>accountId</c> to use a saved
/// account's stored key, or 0 with an explicit name and key for an account that is not saved yet.
/// </summary>
public sealed class StreamContainers(AppDatabase database, SecretProtector secrets, RepositoryProviderRegistry registry) : IHubHandler
{
    public async IAsyncEnumerable<string> HandleAsync(
        long accountId,
        string? accountName,
        string? accountKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string name;
        string? key;
        if (accountId > 0)
        {
            var account = database.GetAccount(accountId);
            if (account is null) yield break;
            name = account.Name;
            key = secrets.Unprotect(account.EncryptedAccountKey);
        }
        else
        {
            name = accountName ?? string.Empty;
            key = accountKey;
        }

        var mediator = registry.GetRepositoryLessProvider().GetRequiredService<IMediator>();
        await foreach (var container in mediator.CreateStream(new ContainerNamesQuery(name, key), cancellationToken).ConfigureAwait(false))
            yield return container;
    }
}
