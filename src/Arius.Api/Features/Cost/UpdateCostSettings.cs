using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Cost;

/// <summary><c>PUT /settings/cost</c></summary>
internal static class UpdateCostSettings
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPut("/settings/cost", (CostSettings settings, AppDatabase db) =>
        {
            if (settings.AutoAcceptBelow is < 0 or > 1000 || decimal.Round(settings.AutoAcceptBelow, 2) != settings.AutoAcceptBelow)
                return Results.BadRequest("Enter an amount from 0 to 1000 EUR, to the cent.");

            db.SaveCostSettings(settings);
            return Results.Ok(settings);
        });
}
