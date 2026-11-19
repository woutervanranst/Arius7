using System.Text.Json;
using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Restore;

/// <summary>
/// Re-drives rehydrating restores until they complete. Every minute it rebuilds its work list from
/// <see cref="Arius.Api.Shared.AppData.AppDatabase.ListActiveRehydrations"/>, so pending restores survive a restart
/// without per-job timers, and calls <see cref="JobRunner.ResumeRestoreAsync"/> for each auto-resume job whose
/// <see cref="RehydrationSchedule"/> is due. Jobs with auto-resume off are resumed manually.
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
        while (await timer.SafeWaitForNextTickAsync(stoppingToken));
    }

    private void Tick()
    {
        var database = services.GetRequiredService<Arius.Api.Shared.AppData.AppDatabase>();
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
}
