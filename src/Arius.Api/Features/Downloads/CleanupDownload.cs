using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Downloads;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Downloads;

/// <summary>Deletes a finished download's staging folder.</summary>
internal sealed class CleanupDownload(AppDatabase db, DownloadStaging staging) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/jobs/{id}/download/cleanup", (string id, CleanupDownload endpoint) => endpoint.Handle(id))
            .WithSummary("Clean up a download")
            .WithDescription("Deletes the staging folder of a finished download job. Returns 409 Conflict while the job is still active, and 404 Not Found if the job does not exist or is not a download job.");

    private IResult Handle(string id)
    {
        var job = db.GetDownloadJob(id);
        if (job is null) return Results.NotFound();
        if (!DownloadEndpoints.IsTerminal(job)) return Results.Conflict();

        staging.Delete(id);
        return Results.NoContent();
    }
}
