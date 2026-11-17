using Arius.Api.AppData;

namespace Arius.Api.Endpoints;

/// <summary>The server-wide cost confirmation setting (Settings › Restore › Cost).</summary>
internal static class CostSettingsEndpoints
{
    public static void MapCostSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/settings/cost", (AppDatabase db) => Results.Ok(db.GetCostSettings()));

        app.MapPut("/settings/cost", (CostSettings settings, AppDatabase db) =>
        {
            if (settings.AutoAcceptBelow is < 0 or > 1000 || decimal.Round(settings.AutoAcceptBelow, 2) != settings.AutoAcceptBelow)
                return Results.BadRequest("Enter an amount from 0 to 1000 EUR, to the cent.");

            db.SaveCostSettings(settings);
            return Results.Ok(settings);
        });
    }
}
