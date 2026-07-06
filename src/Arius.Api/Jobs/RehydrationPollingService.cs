using System.Text.Json;

namespace Arius.Api.Jobs;

/// <summary>
/// Re-drives pending (rehydrating) restores until they complete. Wakes every minute, rebuilds its work list from
/// <see cref="AppData.AppDatabase.ListActiveRehydrations"/> (no per-job timers → survives restart; re-arms every
/// rehydrating row on startup), and for each auto-resume job whose adaptive cadence is due, calls
/// <see cref="JobRunner.ResumeRestoreAsync"/>. Auto-resume=off jobs are skipped (a manual "Restore now" drives them).
/// Mirrors <see cref="SchedulerService"/>. Design §7.
/// </summary>
public sealed class RehydrationPollingService(IServiceProvider services, ILogger<RehydrationPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { Tick(); }
            catch (Exception ex) { logger.LogError(ex, "Rehydration poll tick failed"); }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private void Tick()
    {
        var database = services.GetRequiredService<AppData.AppDatabase>();
        var runner   = services.GetRequiredService<JobRunner>();
        var now      = DateTimeOffset.UtcNow;

        foreach (var job in database.ListActiveRehydrations())
        {
            if (job.StateJson is null) continue;
            PersistedJobState? persisted;
            try { persisted = JsonSerializer.Deserialize<PersistedJobState>(job.StateJson); }
            catch (JsonException) { continue; }
            var resume = persisted?.Resume;
            if (resume is null || !resume.AutoResume) continue;

            var firstChunkSeen = resume.RehydratedCount > 0;
            if (!RehydrationSchedule.IsDue(now, resume.RehydrationStartedAt, resume.LastRunAt, resume.Priority, firstChunkSeen))
                continue;

            logger.LogInformation("Re-driving rehydrating restore {JobId} (repo {RepositoryId})", job.Id, job.RepositoryId);
            _ = runner.ResumeRestoreAsync(job.Id);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try { return await timer.WaitForNextTickAsync(token); }
        catch (OperationCanceledException) { return false; }
    }
}
