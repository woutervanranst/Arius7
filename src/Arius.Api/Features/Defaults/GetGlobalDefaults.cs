using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Defaults;

/// <summary><c>GET /settings/defaults</c></summary>
internal static class GetGlobalDefaults
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/settings/defaults", (AppDatabase db) => Results.Ok(DefaultsEndpoints.ToGlobalDto(db)));
}
