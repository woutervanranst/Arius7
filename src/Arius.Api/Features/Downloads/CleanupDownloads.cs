using Arius.Api.Shared.Downloads;

namespace Arius.Api.Features.Downloads;

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
