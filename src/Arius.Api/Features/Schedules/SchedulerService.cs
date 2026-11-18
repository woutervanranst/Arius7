using Arius.Api.AppData;
using Cronos;

namespace Arius.Api.Jobs;

/// <summary>
/// Fires cron archive schedules. Wakes every minute, computes each enabled schedule's next run with
/// Cronos, and enqueues an archive job when due. Lightweight by design (a hosted BackgroundService
/// rather than Quartz) — sufficient for a handful of per-repo schedules.
/// </summary>
public sealed class SchedulerService(IServiceProvider services, ILogger<SchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger startup so the app finishes booting first.
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { Tick(); }
            catch (Exception ex) { logger.LogError(ex, "Scheduler tick failed"); }
        }
        while (await timer.SafeWaitForNextTickAsync(stoppingToken));
    }

    private void Tick()
    {
        // Resolve scoped-ish singletons fresh each tick (all are singletons here).
        var database = services.GetRequiredService<AppData.AppDatabase>();
        var runner = services.GetRequiredService<JobRunner>();
        var now = DateTimeOffset.UtcNow;

        foreach (var schedule in database.ListSchedules().Where(s => s.Enabled))
        {
            if (!TryParse(schedule.Cron, out var expression))
            {
                logger.LogWarning("Schedule {Id} has an invalid cron expression '{Cron}'", schedule.Id, schedule.Cron);
                continue;
            }

            if (schedule.NextRun is null)
            {
                database.SetScheduleNextRun(schedule.Id, expression.GetNextOccurrence(now, TimeZoneInfo.Utc));
                continue;
            }

            if (now < schedule.NextRun.Value)
                continue;

            // A failure for one schedule (e.g. a corrupt stored default) must not starve the others, and the
            // schedule still advances so it is not retried every minute.
            try
            {
                var repo = database.GetRepository(schedule.RepositoryId);
                if (repo is not null && !database.HasActiveJob(schedule.RepositoryId))
                {
                    var jobId = Guid.NewGuid().ToString();
                    logger.LogInformation("Firing scheduled archive for repository {RepositoryId} (job {JobId})", schedule.RepositoryId, jobId);
                    var (tier, removeLocal, writePointers, fastHash) = ScheduledArchiveArgs(database, schedule.RepositoryId);
                    _ = runner.RunArchiveAsync(schedule.RepositoryId, jobId, tier, removeLocal, writePointers, fastHash, trigger: "schedule");
                }
                else if (repo is not null)
                {
                    logger.LogWarning("Skipped scheduled archive for repository {RepositoryId} — a job is already in progress", schedule.RepositoryId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled archive for repository {RepositoryId} (schedule {ScheduleId}) failed to start", schedule.RepositoryId, schedule.Id);
            }
            database.SetScheduleNextRun(schedule.Id, expression.GetNextOccurrence(now, TimeZoneInfo.Utc));
        }
    }

    /// <summary>The archive options a scheduled run uses: the repository's resolved defaults mapped to archive flags.</summary>
    internal static (string Tier, bool RemoveLocal, bool WritePointers, bool FastHash) ScheduledArchiveArgs(AppDatabase database, long repositoryId)
    {
        var defaults = database.ResolveRepositoryDefaults(repositoryId);
        var (removeLocal, writePointers) = OptionDefaults.ToArchiveFlags(defaults.OnDisk.Value);
        return (defaults.Tier.Value, removeLocal, writePointers, defaults.FastHash.Value);
    }

    private static bool TryParse(string cron, out CronExpression expression)
    {
        try { expression = CronExpression.Parse(cron); return true; }
        catch (CronFormatException) { expression = null!; return false; }
    }
}
