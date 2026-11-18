using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Downloads;

namespace Arius.Api.Features.Downloads;

/// <summary><c>GET /repos/{id}/downloads/pending</c> — the newest finished download with staged files not yet fetched.</summary>
internal static class GetPendingDownload
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/repos/{id:long}/downloads/pending", (long id, AppDatabase db, DownloadStaging staging) =>
        {
            foreach (var job in db.ListDownloadJobs(id))
            {
                if (DownloadEndpoints.IsCleanedUp(job, staging) || !DownloadEndpoints.IsTerminal(job)) continue;

                var files     = db.ListDownloadFiles(job.Id);
                var remaining = files.Where(f => f.Ready && !f.Downloaded).ToList();
                if (remaining.Count == 0) continue;

                return Results.Ok(new PendingDownloadDto(job.Id, files.Count(f => f.Ready), files.Count, remaining.Count, remaining.Sum(f => f.Size)));
            }
            return Results.NoContent();
        });
}
