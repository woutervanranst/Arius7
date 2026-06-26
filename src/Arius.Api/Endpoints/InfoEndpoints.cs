using Arius.Api.Contracts;
using Arius.Core.Shared;

namespace Arius.Api.Endpoints;

/// <summary>Exposes the running backend's build version (the git tag of the deployed image).</summary>
internal static class InfoEndpoints
{
    public static void MapInfoEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/info", () => new AppInfoDto(AriusVersion.Display));
}
