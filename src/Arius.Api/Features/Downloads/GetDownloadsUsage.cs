using Arius.Api.Downloads;

namespace Arius.Api.Endpoints;

/// <summary><c>GET /downloads/usage</c> — staging folders on the server disk and their total size.</summary>
internal static class GetDownloadsUsage
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/downloads/usage", (DownloadStaging staging) => Results.Ok(DownloadEndpoints.Usage(staging)));
}
