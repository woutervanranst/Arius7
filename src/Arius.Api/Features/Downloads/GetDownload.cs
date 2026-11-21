using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Downloads;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Downloads;

/// <summary>Returns a download job's files and clean-up state.</summary>
internal sealed class GetDownload(AppDatabase db, DownloadStaging staging) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/jobs/{id}/download", (string id, GetDownload endpoint) => endpoint.Handle(id))
            .WithSummary("Get a download's files")
            .WithDescription("Returns the files a download job collected, whether each is staged and fetched, whether the staging folder has been cleaned up, and when automatic clean-up will delete it. Returns 404 Not Found if the job does not exist or is not a download job.");

    private IResult Handle(string id)
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
    }
}
