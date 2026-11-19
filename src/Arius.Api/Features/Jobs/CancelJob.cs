using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Jobs;

/// <summary>Cancels a job. A job waiting at the cost prompt is declined, a running job is cancelled cooperatively,
/// and a parked job (no live run) is marked cancelled so the poller skips it and the repository's single-active-job
/// guard is released.</summary>
public sealed class CancelJob(RestoreApprovalRegistry approvals, JobStateRegistry jobStates, JobRunner jobRunner) : IHubHandler
{
    public Task HandleAsync(string jobId)
    {
        // Decline a pending cost approval before cancelling the run: cancelling the token first would end the
        // approval wait as a timeout, which parks the job instead of cancelling it.
        if (approvals.HasPending(jobId)) { approvals.Resolve(jobId, null); return Task.CompletedTask; }
        if (jobStates.CancelLive(jobId)) return Task.CompletedTask;   // running job: cooperative cancellation
        approvals.Resolve(jobId, null);                               // defensive: releases any remaining approval wait
        jobRunner.CancelParked(jobId);                               // marks the job cancelled and broadcasts Done
        return Task.CompletedTask;
    }
}
