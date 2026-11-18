using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Downloads;

namespace Arius.Api.Features.Downloads;

/// <summary>
/// Deletes download staging folders. A folder is removable when its job row is gone (an orphan) or terminal —
/// never while the job is <c>running</c>, <c>awaiting-cost</c> or <c>rehydrating</c>. Per-folder failures are logged
/// and skipped so one locked folder cannot abandon the rest of the batch.
/// </summary>
internal sealed class DownloadCleaner(AppDatabase database, DownloadStaging staging, ILogger<DownloadCleaner> logger)
{
    /// <summary>Deletes every removable staging folder (Settings → Clean up all now); returns how many were deleted.</summary>
    public int CleanUpAll() => CleanUp(_ => true);

    /// <summary>Deletes removable folders whose job finished at least the configured number of days before <paramref name="now"/>
    /// (orphans immediately); no-op when clean-up is disabled. Returns how many were deleted.</summary>
    public int CleanUpExpired(DateTimeOffset now)
    {
        var settings = database.GetDownloadSettings();
        if (!settings.CleanupEnabled)
            return 0;

        var cutoff = now.AddDays(-settings.CleanupDays);
        return CleanUp(job => job is null || job.FinishedAt <= cutoff);
    }

    private int CleanUp(Func<JobRecord?, bool> isDue)
    {
        var deleted = 0;
        foreach (var jobId in staging.ListJobIds())
        {
            try
            {
                var job = database.GetJob(jobId);
                if (job is not null && !JobStatuses.Terminal.Contains(job.Status))
                    continue;
                if (!isDue(job))
                    continue;

                staging.Delete(jobId);
                deleted++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to clean up download folder {JobId}", jobId);
            }
        }
        return deleted;
    }
}
