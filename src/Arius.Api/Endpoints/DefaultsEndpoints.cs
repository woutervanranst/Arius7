using Arius.Api.AppData;
using Arius.Api.Contracts;

namespace Arius.Api.Endpoints;

/// <summary>Global and per-repository archive/restore option defaults.</summary>
internal static class DefaultsEndpoints
{
    public static void MapDefaultsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/settings/defaults", (AppDatabase db) => Results.Ok(ToGlobalDto(db)));

        app.MapPut("/settings/defaults", (DefaultValues values, AppDatabase db) =>
        {
            if (OptionDefaults.Validate(values) is { } error)
                return Results.BadRequest(error);

            db.ReplaceGlobalDefaults(values);
            return Results.Ok(ToGlobalDto(db));
        });

        app.MapGet("/repos/{id:long}/defaults", (long id, AppDatabase db) =>
            db.GetRepository(id) is null ? Results.NotFound() : Results.Ok(ToRepositoryDto(db, id)));

        app.MapPut("/repos/{id:long}/defaults", (long id, DefaultValues values, AppDatabase db) =>
        {
            if (db.GetRepository(id) is null)
                return Results.NotFound();
            if (OptionDefaults.Validate(values) is { } error)
                return Results.BadRequest(error);

            db.ReplaceRepositoryDefaults(id, values);
            return Results.Ok(ToRepositoryDto(db, id));
        });
    }

    private static GlobalDefaultsDto ToGlobalDto(AppDatabase db) => new(
        db.GetGlobalDefaults(),
        OptionDefaults.Factory,
        OptionDefaults.OptionNames.ToDictionary(option => option, db.ListAliasesInheriting));

    private static RepositoryDefaultsDto ToRepositoryDto(AppDatabase db, long id) => new(
        db.GetRepositoryDefaults(id),
        OptionDefaults.Resolve(db.GetGlobalDefaults(), DefaultValues.None));
}
