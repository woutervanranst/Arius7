namespace Arius.Api.Endpoints;

/// <summary><c>GET /health</c> — liveness probe.</summary>
internal static class GetHealth
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
}
