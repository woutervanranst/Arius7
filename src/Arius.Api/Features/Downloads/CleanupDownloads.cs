using Arius.Api.Shared.Downloads;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Downloads;

/// <summary>Cleans up every finished download and returns the remaining usage.</summary>
internal sealed class CleanupDownloads(DownloadCleaner cleaner, DownloadStaging staging) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/downloads/cleanup", (CleanupDownloads endpoint) => endpoint.Handle());

    private IResult Handle()
    {
        cleaner.CleanUpAll();
        return Results.Ok(DownloadEndpoints.Usage(staging));
    }
}
