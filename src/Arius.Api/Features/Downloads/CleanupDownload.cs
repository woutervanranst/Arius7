using Arius.Api.AppData;
using Arius.Api.Downloads;

namespace Arius.Api.Endpoints;

/// <summary><c>POST /jobs/{id}/download/cleanup</c> — deletes a finished download's staging folder.</summary>
internal static class CleanupDownload
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/jobs/{id}/download/cleanup", (string id, AppDatabase db, DownloadStaging staging) =>
        {
            var job = db.GetDownloadJob(id);
            if (job is null) return Results.NotFound();
            if (!DownloadEndpoints.IsTerminal(job)) return Results.Conflict();

            staging.Delete(id);
            return Results.NoContent();
        });
}
