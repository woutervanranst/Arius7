using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Accounts;

internal sealed class UpdateAccount(AppDatabase db, SecretProtector secrets, RepositoryProviderRegistry registry) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPatch("/accounts/{id:long}", (long id, UpdateAccountRequest request, UpdateAccount endpoint) => endpoint.Handle(id, request));

    // Cached providers hold the old key; evict them so they rebuild with the new one.
    private IResult Handle(long id, UpdateAccountRequest request)
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
