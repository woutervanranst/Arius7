namespace Arius.Api.Features.Cost;

/// <summary>The server-wide cost confirmation setting (Settings › Restore › Cost).</summary>
internal static class CostSettingsEndpoints
{
    public static void MapCostSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        GetCostSettings.Map(app);
        UpdateCostSettings.Map(app);
    }
}
