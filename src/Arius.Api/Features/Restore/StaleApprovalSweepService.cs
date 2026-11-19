using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Restore;

/// <summary>
/// Cancels restores abandoned at the cost prompt. Every hour it declines each <c>awaiting-cost</c> job older than
/// <see cref="MaxApprovalAge"/>: a live run through <see cref="RestoreApprovalRegistry.Resolve"/>, so its own
/// decline branch marks it cancelled, and a job without a live wait through <see cref="JobRunner.CancelParked"/>.
/// This is the only place that cleans up abandoned prompts; a dropped client connection deliberately does not.
/// </summary>
internal sealed class StaleApprovalSweepService(IServiceProvider services, ILogger<StaleApprovalSweepService> logger) : BackgroundService
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

    internal void Sweep(DateTimeOffset cutoff)
    {
        var database  = services.GetRequiredService<AppDatabase>();
        var approvals = services.GetRequiredService<RestoreApprovalRegistry>();
        var runner    = services.GetRequiredService<JobRunner>();

        foreach (var job in database.ListStaleAwaitingCost(cutoff))
        {
            // One failing job must not stop the rest of the sweep.
            try
            {
                logger.LogInformation("Auto-cancelling abandoned awaiting-cost job {JobId} (older than {Age})", job.Id, MaxApprovalAge);
                if (approvals.HasPending(job.Id))
                    approvals.Resolve(job.Id, null);
                else
                    runner.CancelParked(job.Id, "Cost approval abandoned.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to auto-cancel stale job {JobId}", job.Id);
            }
        }
    }
}
