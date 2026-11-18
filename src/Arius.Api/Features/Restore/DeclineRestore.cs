using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Restore;

/// <summary>Hub <c>DeclineRestore</c> — declines the restore cost modal (equivalent to answering "cancel").
/// Resolves a live wait, or marks a parked job cancelled.</summary>
public sealed class DeclineRestore(RestoreApprovalRegistry approvals, JobRunner jobRunner) : IHubHandler
{
    public Task HandleAsync(string jobId)
    {
        if (approvals.HasPending(jobId)) { approvals.Resolve(jobId, null); return Task.CompletedTask; }

        jobRunner.CancelParked(jobId);   // mark cancelled + broadcast Done
        return Task.CompletedTask;
    }
}
