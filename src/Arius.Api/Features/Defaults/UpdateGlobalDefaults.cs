using Arius.Api.AppData;

namespace Arius.Api.Endpoints;

/// <summary><c>PUT /settings/defaults</c></summary>
internal static class UpdateGlobalDefaults
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPut("/settings/defaults", (DefaultValues values, AppDatabase db) =>
        {
            if (OptionDefaults.Validate(values) is { } error)
                return Results.BadRequest(error);

            db.ReplaceGlobalDefaults(values);
            return Results.Ok(DefaultsEndpoints.ToGlobalDto(db));
        });
}
