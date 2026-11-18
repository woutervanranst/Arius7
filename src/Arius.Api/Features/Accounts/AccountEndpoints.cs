using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Accounts;

/// <summary>Helpers shared by the <c>/accounts</c> endpoints.</summary>
internal static class AccountEndpoints
{
    internal static AccountDto ToDto(AppDatabase db, AccountRecord account)
        => new(account.Id, account.Name, db.CountRepositoriesForAccount(account.Id), account.EncryptedAccountKey is not null);
}
