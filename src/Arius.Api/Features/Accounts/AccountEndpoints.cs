using Arius.Api.AppData;
using Arius.Api.Contracts;

namespace Arius.Api.Endpoints;

/// <summary>Storage-account CRUD over the app database: the <c>/accounts</c> route group.</summary>
internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/accounts");

        ListAccounts.Map(group);
        GetAccount.Map(group);
        CreateAccount.Map(group);
        UpdateAccount.Map(group);
        DeleteAccount.Map(group);
    }

    internal static AccountDto ToDto(AppDatabase db, AccountRecord account)
        => new(account.Id, account.Name, db.CountRepositoriesForAccount(account.Id), account.EncryptedAccountKey is not null);
}
