using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Accounts;

/// <summary><c>PATCH /accounts/{id}</c> — account-flyout edit: rotate the key.</summary>
internal sealed class UpdateAccount(AppDatabase db, SecretProtector secrets, RepositoryProviderRegistry registry) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPatch("/accounts/{id:long}", (long id, UpdateAccountRequest request, UpdateAccount endpoint) => endpoint.Handle(id, request));

    // A null key in the request leaves the stored key unchanged;
    // rotating it invalidates cached providers so the new key takes effect on rebuild.
    public IResult Handle(long id, UpdateAccountRequest request)
    {
        if (db.GetAccount(id) is null)
            return Results.NotFound();

        var keyChanged = request.AccountKey is not null;

        db.UpdateAccount(id, secrets.Protect(request.AccountKey));

        if (keyChanged)
            foreach (var repoId in db.ListRepositoryIdsForAccount(id))
                registry.Evict(repoId);

        return Results.Ok(AccountEndpoints.ToDto(db, db.GetAccount(id)!));
    }
}
