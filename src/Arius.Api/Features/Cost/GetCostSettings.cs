using Arius.Api.AppData;

namespace Arius.Api.Endpoints;

/// <summary><c>GET /settings/cost</c></summary>
internal static class GetCostSettings
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/settings/cost", (AppDatabase db) => Results.Ok(db.GetCostSettings()));
}
