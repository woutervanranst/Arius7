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
        while (await SafeWaitAsync(timer, stoppingToken));
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

            // Due: fire an archive job and roll the next occurrence forward.
            var repo = database.GetRepository(schedule.RepositoryId);
            if (repo is not null)
            {
                var jobId = Guid.NewGuid().ToString();
                logger.LogInformation("Firing scheduled archive for repository {RepositoryId} (job {JobId})", schedule.RepositoryId, jobId);
                _ = runner.RunArchiveAsync(schedule.RepositoryId, jobId, repo.DefaultTier, removeLocal: false, writePointers: false, trigger: "schedule");
            }
            database.SetScheduleNextRun(schedule.Id, expression.GetNextOccurrence(now, TimeZoneInfo.Utc));
        }
    }

    private static bool TryParse(string cron, out CronExpression expression)
    {
        try { expression = CronExpression.Parse(cron); return true; }
        catch (CronFormatException) { expression = null!; return false; }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try { return await timer.WaitForNextTickAsync(token); }
        catch (OperationCanceledException) { return false; }
    }
}
