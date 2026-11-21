using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Repositories;

internal sealed class CreateRepository(AppDatabase db, SecretProtector secrets) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/repos/", (CreateRepositoryRequest request, CreateRepository endpoint) => endpoint.Handle(request))
            .WithSummary("Add a repository")
            .WithDescription("Registers a blob container of a saved storage account as a repository and returns it with 201 Created. The container does not have to exist yet: the first archive creates it. The passphrase is encrypted at rest. The pricing region in the response is null; it is resolved by the next list request. Returns 400 Bad Request if the account does not exist.");

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
