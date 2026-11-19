using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Downloads;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Downloads;

/// <summary><c>POST /jobs/{id}/download/cleanup</c> — deletes a finished download's staging folder.</summary>
internal sealed class CleanupDownload(AppDatabase db, DownloadStaging staging) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/jobs/{id}/download/cleanup", (string id, CleanupDownload endpoint) => endpoint.Handle(id));

    private IResult Handle(string id)
    {
        var job = db.GetDownloadJob(id);
        if (job is null) return Results.NotFound();
        if (!DownloadEndpoints.IsTerminal(job)) return Results.Conflict();

        staging.Delete(id);
        return Results.NoContent();
    }
}
