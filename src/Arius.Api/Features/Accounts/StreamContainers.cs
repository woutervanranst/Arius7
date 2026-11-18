using System.Runtime.CompilerServices;
using Arius.Api.Shared.AppData;
using Arius.Core.Shared.Storage;

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
        IBlobServiceFactory blobServiceFactory,
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

        var blobService = await blobServiceFactory.CreateAsync(name, key, cancellationToken).ConfigureAwait(false);
        await foreach (var container in blobService.GetContainerNamesAsync(cancellationToken).ConfigureAwait(false))
            yield return container;
    }
}
