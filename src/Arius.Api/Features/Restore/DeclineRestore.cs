using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Restore;

/// <summary>Declines a restore's cost estimate: resolves a live approval wait, or marks a parked job cancelled.</summary>
public sealed class DeclineRestore(RestoreApprovalRegistry approvals, JobRunner jobRunner) : IHubHandler
{
    public Task HandleAsync(string jobId)
    {
        if (approvals.HasPending(jobId)) { approvals.Resolve(jobId, null); return Task.CompletedTask; }

        jobRunner.CancelParked(jobId);
        return Task.CompletedTask;
    }
}
