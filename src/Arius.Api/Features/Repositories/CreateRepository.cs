using Arius.Api.AppData;
using Arius.Api.Contracts;

namespace Arius.Api.Endpoints;

/// <summary><c>POST /repos</c></summary>
internal static class CreateRepository
{
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapPost("/", (CreateRepositoryRequest request, AppDatabase db, SecretProtector secrets) =>
        {
            if (db.GetAccount(request.AccountId) is null)
                return Results.BadRequest($"Account {request.AccountId} does not exist.");

            var id = db.InsertRepository(
                request.Alias,
                request.Container,
                request.AccountId,
                request.LocalPath,
                secrets.Protect(request.Passphrase));

            // Region is left unresolved here (the container may not exist yet); the client refetches the list, which resolves and caches it.
            return Results.Created($"/repos/{id}", RepositoryEndpoints.ToDto(db, db.GetRepository(id)!, region: null, regionIsDefault: false));
        });
}
