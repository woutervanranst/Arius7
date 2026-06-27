using Arius.Api.Composition;
using Arius.Api.Contracts;
using Arius.Api.AppData;

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
            var id = db.InsertAccount(request.Name, secrets.Protect(request.AccountKey), NormalizeRegion(request.Region));
            var account = db.GetAccount(id)!;
            return Results.Created($"/accounts/{id}", ToDto(db, account));
        });

        // Account-flyout edit: rotate the key and/or change the region. A null key in the request leaves
        // the stored key unchanged. Rotating the key invalidates cached providers; changing the region
        // invalidates memoized statistics (whose cost figures are region-priced) for this account's repos.
        group.MapPatch("/{id:long}", (long id, UpdateAccountRequest request, AppDatabase db, SecretProtector secrets, RepositoryProviderRegistry registry) =>
        {
            var existing = db.GetAccount(id);
            if (existing is null)
                return Results.NotFound();

            var newRegion = NormalizeRegion(request.Region);
            var keyChanged    = request.AccountKey is not null;
            var regionChanged = !string.Equals(existing.Region, newRegion, StringComparison.Ordinal);

            db.UpdateAccount(id, secrets.Protect(request.AccountKey), newRegion);

            if (keyChanged || regionChanged)
            {
                foreach (var repoId in db.ListRepositoryIdsForAccount(id))
                {
                    if (keyChanged) registry.Evict(repoId);          // new key takes effect on rebuild
                    if (regionChanged) db.ClearStatisticsCache(repoId); // cost is region-priced → recompute
                }
            }

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

    /// <summary>Treat blank / "unknown" as no region.</summary>
    private static string? NormalizeRegion(string? region)
        => string.IsNullOrWhiteSpace(region) || region.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : region.Trim();

    private static AccountDto ToDto(AppDatabase db, AccountRecord account)
        => new(account.Id, account.Name, db.CountRepositoriesForAccount(account.Id), account.EncryptedAccountKey is not null, account.Region);
}
