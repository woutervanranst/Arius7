using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Cost;

/// <summary><c>GET /settings/cost</c></summary>
internal sealed class GetCostSettings(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/settings/cost", (GetCostSettings endpoint) => endpoint.Handle());

    public IResult Handle() => Results.Ok(db.GetCostSettings());
}
