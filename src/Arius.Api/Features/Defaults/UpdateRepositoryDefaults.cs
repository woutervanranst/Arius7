using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Defaults;

/// <summary><c>PUT /repos/{id}/defaults</c></summary>
internal sealed class UpdateRepositoryDefaults(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/repos/{id:long}/defaults", (long id, DefaultValues values, UpdateRepositoryDefaults endpoint) => endpoint.Handle(id, values));

    public IResult Handle(long id, DefaultValues values)
    {
        if (db.GetRepository(id) is null)
            return Results.NotFound();
        if (OptionDefaults.Validate(values) is { } error)
            return Results.BadRequest(error);

        db.ReplaceRepositoryDefaults(id, values);
        return Results.Ok(DefaultsEndpoints.ToRepositoryDto(db, id));
    }
}
