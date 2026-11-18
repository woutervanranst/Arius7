using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Downloads;

namespace Arius.Api.Features.Downloads;

/// <summary><c>GET /jobs/{id}/download</c> — a download job's files and clean-up state.</summary>
internal static class GetDownload
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/jobs/{id}/download", (string id, AppDatabase db, DownloadStaging staging) =>
        {
            var job = db.GetDownloadJob(id);
            if (job is null) return Results.NotFound();

            var cleanedUp = DownloadEndpoints.IsCleanedUp(job, staging);
            var settings  = db.GetDownloadSettings();
            DateTimeOffset? cleanupAt = settings.CleanupEnabled && DownloadEndpoints.IsTerminal(job) && !cleanedUp
                ? job.FinishedAt?.AddDays(settings.CleanupDays)
                : null;

            var files = db.ListDownloadFiles(id).Select(f => new DownloadFileDto(f.Path, f.Size, f.Ready, f.Downloaded)).ToList();
            return Results.Ok(new DownloadStateDto(files, cleanedUp, cleanupAt));
        });
}
