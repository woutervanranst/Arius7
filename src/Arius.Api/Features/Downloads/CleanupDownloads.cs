using Arius.Api.Shared.Downloads;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Downloads;

/// <summary>Cleans up every finished download and returns the remaining usage.</summary>
internal sealed class CleanupDownloads(DownloadCleaner cleaner, DownloadStaging staging) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/downloads/cleanup", (CleanupDownloads endpoint) => endpoint.Handle())
            .WithSummary("Clean up all finished downloads")
            .WithDescription("Deletes the staging folders of all finished download jobs, regardless of the clean-up delay, and returns the remaining usage.");

    private DownloadUsageDto Handle()
    {
        cleaner.CleanUpAll();
        return DownloadEndpoints.Usage(staging);
    }
}
