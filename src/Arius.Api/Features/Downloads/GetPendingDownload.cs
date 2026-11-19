using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Downloads;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Downloads;

/// <summary>Returns the repository's newest finished download that still has staged files not yet fetched.</summary>
internal sealed class GetPendingDownload(AppDatabase db, DownloadStaging staging) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/repos/{id:long}/downloads/pending", (long id, GetPendingDownload endpoint) => endpoint.Handle(id));

    private IResult Handle(long id)
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
    }
}
