using Arius.Api.Jobs;

namespace Arius.Api.Hubs;

/// <summary>Hub <c>CancelJob</c> — requests cancellation of a job. A job parked at the cost prompt is cancelled by
/// resolving its wait as a decline (the restore run's decline branch marks it <c>cancelled</c>). A LIVE job with no
/// pending approval is cancelled cooperatively (its token trips at the next checkpoint). A PARKED job
/// (awaiting-cost / rehydrating, no live run) releases any waiting approval and is marked terminal so the poller
/// skips it and the single-active-job guard is freed.</summary>
internal static class CancelJob
{
    public static Task HandleAsync(RestoreApprovalRegistry approvals, JobStateRegistry jobStates, JobRunner jobRunner, string jobId)
    {
        // A job parked at the cost prompt is cancelled by resolving its wait as a decline — the restore run's
        // decline branch then marks it `cancelled`. This MUST precede CancelLive: cancelling the CTS would race
        // the approval wait into a *timeout* (park), not a cancel.
        if (approvals.HasPending(jobId)) { approvals.Resolve(jobId, null); return Task.CompletedTask; }
        if (jobStates.CancelLive(jobId)) return Task.CompletedTask;   // mid-run → cooperative CTS cancel
        approvals.Resolve(jobId, null);                               // parked/not-live safety no-op
        jobRunner.CancelParked(jobId);                               // mark cancelled + broadcast Done
        return Task.CompletedTask;
    }
}
