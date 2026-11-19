using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Accounts;

internal static class AccountEndpoints
{
    internal static AccountDto ToDto(AppDatabase db, AccountRecord account)
        => new(account.Id, account.Name, db.CountRepositoriesForAccount(account.Id), account.EncryptedAccountKey is not null);
}
