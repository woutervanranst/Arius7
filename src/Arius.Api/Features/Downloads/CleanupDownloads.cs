using Arius.Api.Downloads;

namespace Arius.Api.Endpoints;

/// <summary><c>POST /downloads/cleanup</c> — cleans up every finished download and returns the remaining usage.</summary>
internal static class CleanupDownloads
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/downloads/cleanup", (DownloadCleaner cleaner, DownloadStaging staging) =>
        {
            cleaner.CleanUpAll();
            return Results.Ok(DownloadEndpoints.Usage(staging));
        });
}
