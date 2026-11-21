using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Cost;

internal sealed class UpdateCostSettings(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/settings/cost", (CostSettings settings, UpdateCostSettings endpoint) => endpoint.Handle(settings))
            .WithSummary("Update the cost confirmation settings")
            .WithDescription("Returns 400 Bad Request unless the threshold is between 0 and 1000 EUR with at most two decimal places.");

    private IResult Handle(CostSettings settings)
    {
        if (settings.AutoAcceptBelow is < 0 or > 1000 || decimal.Round(settings.AutoAcceptBelow, 2) != settings.AutoAcceptBelow)
            return Results.BadRequest("Enter an amount from 0 to 1000 EUR, to the cent.");

        db.SaveCostSettings(settings);
        return Results.Ok(settings);
    }
}
