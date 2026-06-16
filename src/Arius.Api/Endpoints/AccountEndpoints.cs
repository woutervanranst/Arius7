using Arius.Api.Contracts;
using Arius.Api.Data;

namespace Arius.Api.Endpoints;

/// <summary>Storage-account CRUD over the app database.</summary>
internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/accounts");

        group.MapGet("/", (AppDatabase db) =>
            db.ListAccounts().Select(a => ToDto(db, a)).ToList());

        group.MapPost("/", (CreateAccountRequest request, AppDatabase db, SecretProtector secrets) =>
        {
            var id = db.InsertAccount(request.Name, secrets.Protect(request.AccountKey));
            var account = db.GetAccount(id)!;
            return Results.Created($"/accounts/{id}", ToDto(db, account));
        });
    }

    private static AccountDto ToDto(AppDatabase db, AccountRecord account)
        => new(account.Id, account.Name, db.CountRepositoriesForAccount(account.Id), account.EncryptedAccountKey is not null);
}
