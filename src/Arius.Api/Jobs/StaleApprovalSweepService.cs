using Arius.Api.AppData;

namespace Arius.Api.Jobs;

/// <summary>
/// Auto-cancels restores abandoned at the cost prompt. Wakes hourly and declines any <c>awaiting-cost</c> job
/// older than <see cref="MaxApprovalAge"/> (24h) — the single owner of "closed the modal and walked away"
/// cleanup, deliberately off the hub/connection path (a dropped tab must not decide). A live run
/// (pending approval) is declined via <see cref="RestoreApprovalRegistry.Resolve"/> so its own decline branch
/// marks it cancelled + broadcasts Done; a row with no live wait is cancelled via <see cref="JobRunner.CancelParked"/>.
/// Mirrors <see cref="SchedulerService"/>/<see cref="RehydrationPollingService"/>.
/// </summary>
public sealed class StaleApprovalSweepService(IServiceProvider services, ILogger<StaleApprovalSweepService> logger) : BackgroundService
{
    public static readonly TimeSpan MaxApprovalAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { Sweep(DateTimeOffset.UtcNow - MaxApprovalAge); }
            catch (Exception ex) { logger.LogError(ex, "Stale-approval sweep tick failed"); }
        }
        while (await timer.SafeWaitForNextTickAsync(stoppingToken));
    }

    public void Sweep(DateTimeOffset cutoff)
    {
        var database  = services.GetRequiredService<AppDatabase>();
        var approvals = services.GetRequiredService<RestoreApprovalRegistry>();
        var runner    = services.GetRequiredService<JobRunner>();

        foreach (var job in database.ListStaleAwaitingCost(cutoff))
        {
            logger.LogInformation("Auto-cancelling abandoned awaiting-cost job {JobId} (older than {Age})", job.Id, MaxApprovalAge);
            if (approvals.HasPending(job.Id))
                approvals.Resolve(job.Id, null);                          // live run → decline branch marks cancelled + Done
            else
                runner.CancelParked(job.Id, "Cost approval abandoned.");  // no live wait → cancel + broadcast Done
        }
    }
}
