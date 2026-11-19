using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Health;

/// <summary>Liveness probe.</summary>
internal sealed class GetHealth : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/health", (GetHealth endpoint) => endpoint.Handle());

    private IResult Handle() => Results.Ok(new { status = "ok" });
}
