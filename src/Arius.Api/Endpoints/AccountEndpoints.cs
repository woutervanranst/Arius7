using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.Contracts;

namespace Arius.Api.Endpoints;

/// <summary>Storage-account CRUD over the app database.</summary>
internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/accounts");

        group.MapGet("/", (AppDatabase db) =>
            db.ListAccounts().Select(a => ToDto(db, a)).ToList());

        group.MapGet("/{id:long}", (long id, AppDatabase db) =>
        {
            var account = db.GetAccount(id);
            return account is null ? Results.NotFound() : Results.Ok(ToDto(db, account));
        });

        group.MapPost("/", (CreateAccountRequest request, AppDatabase db, SecretProtector secrets) =>
        {
            var id = db.InsertAccount(request.Name, secrets.Protect(request.AccountKey));
            var account = db.GetAccount(id)!;
            return Results.Created($"/accounts/{id}", ToDto(db, account));
        });

        // Account-flyout edit: rotate the key. A null key in the request leaves the stored key unchanged;
        // rotating it invalidates cached providers so the new key takes effect on rebuild.
        group.MapPatch("/{id:long}", (long id, UpdateAccountRequest request, AppDatabase db, SecretProtector secrets, RepositoryProviderRegistry registry) =>
        {
            if (db.GetAccount(id) is null)
                return Results.NotFound();

            var keyChanged = request.AccountKey is not null;

            db.UpdateAccount(id, secrets.Protect(request.AccountKey));

            if (keyChanged)
                foreach (var repoId in db.ListRepositoryIdsForAccount(id))
                    registry.Evict(repoId);

            return Results.Ok(ToDto(db, db.GetAccount(id)!));
        });

        group.MapDelete("/{id:long}", (long id, AppDatabase db) =>
        {
            if (db.GetAccount(id) is null)
                return Results.NotFound();
            if (db.CountRepositoriesForAccount(id) > 0)
                return Results.Conflict("Account still has repositories.");

            db.DeleteAccount(id);
            return Results.NoContent();
        });
    }

    private static AccountDto ToDto(AppDatabase db, AccountRecord account)
        => new(account.Id, account.Name, db.CountRepositoriesForAccount(account.Id), account.EncryptedAccountKey is not null);
}
