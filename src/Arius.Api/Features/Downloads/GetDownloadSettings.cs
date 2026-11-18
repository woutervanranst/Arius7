using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Downloads;

/// <summary><c>GET /settings/downloads</c></summary>
internal static class GetDownloadSettings
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/settings/downloads", (AppDatabase db) => Results.Ok(db.GetDownloadSettings()));
}
