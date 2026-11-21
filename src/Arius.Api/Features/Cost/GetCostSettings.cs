using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Cost;

internal sealed class GetCostSettings(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/settings/cost", (GetCostSettings endpoint) => endpoint.Handle())
            .WithSummary("Get the cost confirmation settings");

    private CostSettings Handle() => db.GetCostSettings();
}
