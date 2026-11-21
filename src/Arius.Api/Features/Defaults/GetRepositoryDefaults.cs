using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Defaults;

internal sealed class GetRepositoryDefaults(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/repos/{id:long}/defaults", (long id, GetRepositoryDefaults endpoint) => endpoint.Handle(id))
            .WithSummary("Get a repository's defaults")
            .WithDescription("Returns the options the repository sets itself and, for each option, the value it would inherit from the global defaults or factory values. Returns 404 Not Found if the repository does not exist.");

    private IResult Handle(long id) =>
        db.GetRepository(id) is null ? Results.NotFound() : Results.Ok(DefaultsEndpoints.ToRepositoryDto(db, id));
}
