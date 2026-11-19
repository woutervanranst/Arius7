using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Defaults;

internal sealed class GetGlobalDefaults(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/settings/defaults", (GetGlobalDefaults endpoint) => endpoint.Handle());

    private IResult Handle() => Results.Ok(DefaultsEndpoints.ToGlobalDto(db));
}
