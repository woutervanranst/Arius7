using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Cost;

/// <summary><c>PUT /settings/cost</c></summary>
internal sealed class UpdateCostSettings(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/settings/cost", (CostSettings settings, UpdateCostSettings endpoint) => endpoint.Handle(settings));

    public IResult Handle(CostSettings settings)
    {
        if (settings.AutoAcceptBelow is < 0 or > 1000 || decimal.Round(settings.AutoAcceptBelow, 2) != settings.AutoAcceptBelow)
            return Results.BadRequest("Enter an amount from 0 to 1000 EUR, to the cent.");

        db.SaveCostSettings(settings);
        return Results.Ok(settings);
    }
}
