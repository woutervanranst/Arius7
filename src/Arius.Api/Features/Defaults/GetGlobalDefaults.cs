using Arius.Api.AppData;

namespace Arius.Api.Endpoints;

/// <summary><c>GET /settings/defaults</c></summary>
internal static class GetGlobalDefaults
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/settings/defaults", (AppDatabase db) => Results.Ok(DefaultsEndpoints.ToGlobalDto(db)));
}
