using Arius.Core.Shared.Pricing;

namespace Arius.Api.Endpoints;

/// <summary>Exposes the pricing catalog's metadata — currently the set of priced regions for the account dropdown.</summary>
internal static class PricingEndpoints
{
    // The embedded catalog is immutable for the process lifetime; load it once.
    private static readonly IReadOnlyList<string> _regions = PricingCatalog.LoadEmbedded().RegionNames;

    public static void MapPricingEndpoints(this IEndpointRouteBuilder app)
    {
        // The programmatic Azure regions that have pricing. The UI adds an "Unknown / Not in list" option itself.
        app.MapGet("/pricing/regions", () => _regions);
    }
}
