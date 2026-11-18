using System.Runtime.CompilerServices;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Core.Features.ContainerNamesQuery;
using Mediator;

namespace Arius.Api.Features.Accounts;

/// <summary>
/// Hub <c>StreamContainers</c> — streams the container names in an account (Add-existing wizard). Pass
/// <c>accountId</c> &gt; 0 to use a configured account's stored key, or 0 with an explicit name + key for a new account.
/// </summary>
internal static class StreamContainers
{
    public static async IAsyncEnumerable<string> HandleAsync(
        AppDatabase database,
        SecretProtector secrets,
        RepositoryProviderRegistry registry,
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
