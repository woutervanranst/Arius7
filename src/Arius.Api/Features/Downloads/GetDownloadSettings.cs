using Arius.Api.AppData;

namespace Arius.Api.Endpoints;

/// <summary><c>GET /settings/downloads</c></summary>
internal static class GetDownloadSettings
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/settings/downloads", (AppDatabase db) => Results.Ok(db.GetDownloadSettings()));
}
