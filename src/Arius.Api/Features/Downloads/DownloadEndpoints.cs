using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Downloads;
using Microsoft.Net.Http.Headers;

namespace Arius.Api.Features.Downloads;

/// <summary>Helpers shared by the <c>/jobs/{id}/download</c>, <c>/downloads</c> and <c>/settings/downloads</c> endpoints.</summary>
internal static class DownloadEndpoints
{
    /// <summary>Whether a finished transfer counts as the file being downloaded: never when aborted; judged by the range
    /// actually <em>served</em> (<c>null</c> = the whole file), since the request's range can be a suffix or be ignored.</summary>
    internal static bool CountsAsDownloaded(bool aborted, ContentRangeHeaderValue? served, long length) =>
        !aborted && (served is null || served.To >= length - 1);

    internal static DownloadUsageDto Usage(DownloadStaging staging)
    {
        var ids = staging.ListJobIds();
        return new DownloadUsageDto(ids.Count, ids.Sum(staging.SizeOf));
    }

    internal static bool IsTerminal(JobRecord job) => JobStatuses.Terminal.Contains(job.Status);

    internal static bool IsCleanedUp(JobRecord job, DownloadStaging staging) => IsTerminal(job) && !staging.Exists(job.Id);

    /// <summary>The job, only when it exists and is a download — the staging helper trusts its job id, so every route resolves it here first.</summary>
    internal static JobRecord? GetDownloadJob(this AppDatabase db, string id) =>
        db.GetJob(id) is { Kind: "download" } job ? job : null;
}
