using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Repositories;

internal sealed class CreateRepository(AppDatabase db, SecretProtector secrets) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/repos/", (CreateRepositoryRequest request, CreateRepository endpoint) => endpoint.Handle(request));

    private IResult Handle(CreateRepositoryRequest request)
    {
        if (db.GetAccount(request.AccountId) is null)
            return Results.BadRequest($"Account {request.AccountId} does not exist.");

        var id = db.InsertRepository(
            request.Alias,
            request.Container,
            request.AccountId,
            request.LocalPath,
            secrets.Protect(request.Passphrase));

        // The region stays unresolved because the container may not exist yet; it is resolved and cached when the
        // client refetches the list.
        return Results.Created($"/repos/{id}", RepositoryEndpoints.ToDto(db, db.GetRepository(id)!, region: null, regionIsDefault: false));
    }
}
