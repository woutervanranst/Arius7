using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Health;

/// <summary>Liveness probe.</summary>
internal sealed class GetHealth : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/health", (GetHealth endpoint) => endpoint.Handle())
            .WithSummary("Check service health")
            .WithDescription("Returns 200 OK while the service is running. Like every `/api` response, it carries the server version in the `X-Arius-Version` header.");

    private IResult Handle() => Results.Ok(new { status = "ok" });
}
