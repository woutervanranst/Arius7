using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;

namespace Arius.Api.Features.Accounts;

/// <summary><c>PATCH /accounts/{id}</c> — account-flyout edit: rotate the key.</summary>
internal static class UpdateAccount
{
    // A null key in the request leaves the stored key unchanged;
    // rotating it invalidates cached providers so the new key takes effect on rebuild.
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapPatch("/{id:long}", (long id, UpdateAccountRequest request, AppDatabase db, SecretProtector secrets, RepositoryProviderRegistry registry) =>
        {
            if (db.GetAccount(id) is null)
                return Results.NotFound();

            var keyChanged = request.AccountKey is not null;

            db.UpdateAccount(id, secrets.Protect(request.AccountKey));

            if (keyChanged)
                foreach (var repoId in db.ListRepositoryIdsForAccount(id))
                    registry.Evict(repoId);

            return Results.Ok(AccountEndpoints.ToDto(db, db.GetAccount(id)!));
        });
}
