using Arius.Api.AppData;

namespace Arius.Api.Endpoints;

/// <summary><c>PUT /repos/{id}/defaults</c></summary>
internal static class UpdateRepositoryDefaults
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPut("/repos/{id:long}/defaults", (long id, DefaultValues values, AppDatabase db) =>
        {
            if (db.GetRepository(id) is null)
                return Results.NotFound();
            if (OptionDefaults.Validate(values) is { } error)
                return Results.BadRequest(error);

            db.ReplaceRepositoryDefaults(id, values);
            return Results.Ok(DefaultsEndpoints.ToRepositoryDto(db, id));
        });
}
