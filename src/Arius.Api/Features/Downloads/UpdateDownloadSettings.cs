using Arius.Api.AppData;

namespace Arius.Api.Endpoints;

/// <summary><c>PUT /settings/downloads</c></summary>
internal static class UpdateDownloadSettings
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPut("/settings/downloads", (DownloadSettings settings, AppDatabase db) =>
        {
            if (settings.CleanupDays is < 1 or > 3650)
                return Results.BadRequest("Clean-up days must be between 1 and 3650.");

            db.SaveDownloadSettings(settings);
            return Results.Ok(settings);
        });
}
