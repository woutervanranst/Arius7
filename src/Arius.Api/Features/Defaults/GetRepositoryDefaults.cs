using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Arius.Api.Features.Defaults;

/// <summary><c>GET /repos/{id}/defaults</c></summary>
internal sealed class GetRepositoryDefaults(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/repos/{id:long}/defaults", (long id, GetRepositoryDefaults endpoint) => endpoint.Handle(id))
            .WithSummary("Get a repository's defaults")
            .WithDescription("Returns the options the repository sets itself and, for each option, the value it would inherit from the global defaults or factory values. Returns 404 Not Found if the repository does not exist.");

    private Results<Ok<RepositoryDefaultsDto>, NotFound> Handle(long id) =>
        db.GetRepository(id) is null ? TypedResults.NotFound() : TypedResults.Ok(DefaultsEndpoints.ToRepositoryDto(db, id));
}
