using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Cost;

/// <summary><c>GET /settings/cost</c></summary>
internal static class GetCostSettings
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/settings/cost", (AppDatabase db) => Results.Ok(db.GetCostSettings()));
}
