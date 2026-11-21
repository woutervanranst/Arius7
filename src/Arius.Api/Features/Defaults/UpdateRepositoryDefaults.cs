using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Defaults;

internal sealed class UpdateRepositoryDefaults(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/repos/{id:long}/defaults", (long id, DefaultValues values, UpdateRepositoryDefaults endpoint) => endpoint.Handle(id, values))
            .WithSummary("Replace a repository's defaults")
            .WithDescription("Replaces all of the repository's own defaults. A null option is unset and is inherited from the global defaults. Returns 400 Bad Request for an unknown tier or on-disk value, and 404 Not Found if the repository does not exist.");

    private IResult Handle(long id, DefaultValues values)
    {
        if (db.GetRepository(id) is null)
            return Results.NotFound();
        if (OptionDefaults.Validate(values) is { } error)
            return Results.BadRequest(error);

        db.ReplaceRepositoryDefaults(id, values);
        return Results.Ok(DefaultsEndpoints.ToRepositoryDto(db, id));
    }
}
