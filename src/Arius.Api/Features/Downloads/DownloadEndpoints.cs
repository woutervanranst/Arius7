using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Downloads;
using Microsoft.Net.Http.Headers;

namespace Arius.Api.Features.Downloads;

internal static class DownloadEndpoints
{
    /// <summary>Whether a finished transfer counts as a completed download: never when aborted, otherwise when the
    /// served range (<c>null</c> for the whole file) reaches the last byte. The served range is used because the
    /// requested range can be a suffix or be ignored.</summary>
    internal static bool CountsAsDownloaded(bool aborted, ContentRangeHeaderValue? served, long length) =>
        !aborted && (served is null || served.To >= length - 1);

    internal static DownloadUsageDto Usage(DownloadStaging staging)
    {
        var ids = staging.ListJobIds();
        return new DownloadUsageDto(ids.Count, ids.Sum(staging.SizeOf));
    }

    internal static bool IsTerminal(JobRecord job) => JobStatuses.Terminal.Contains(job.Status);

    internal static bool IsCleanedUp(JobRecord job, DownloadStaging staging) => IsTerminal(job) && !staging.Exists(job.Id);

    /// <summary>Returns the job only when it is a download job. <see cref="DownloadStaging"/> trusts the job id it is
    /// given, so every download route resolves the job here first.</summary>
    internal static JobRecord? GetDownloadJob(this AppDatabase db, string id) =>
        db.GetJob(id) is { Kind: "download" } job ? job : null;
}
