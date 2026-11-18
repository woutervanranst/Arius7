using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Repositories;

/// <summary><c>POST /repos</c></summary>
internal sealed class CreateRepository(AppDatabase db, SecretProtector secrets) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/repos/", (CreateRepositoryRequest request, CreateRepository endpoint) => endpoint.Handle(request));

    public IResult Handle(CreateRepositoryRequest request)
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
    }
}
