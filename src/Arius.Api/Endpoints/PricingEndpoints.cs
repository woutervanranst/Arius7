using Arius.Core.Shared.Cost;

namespace Arius.Api.Endpoints;

/// <summary>Exposes the storage provider's pricing metadata — currently the set of priced regions for the account dropdown.</summary>
internal static class PricingEndpoints
{
    public static void MapPricingEndpoints(this IEndpointRouteBuilder app)
    {
        // The programmatic regions that have pricing. The UI adds an "Unknown / Not in list" option itself.
        app.MapGet("/pricing/regions", (IStorageCostEstimator estimator) => estimator.Regions);
    }
}
