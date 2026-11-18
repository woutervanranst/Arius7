using Arius.Api.Jobs;

namespace Arius.Api.Hubs;

/// <summary>Hub <c>DeclineRestore</c> — declines the restore cost modal (equivalent to answering "cancel").
/// Resolves a live wait, or marks a parked job cancelled.</summary>
internal static class DeclineRestore
{
    public static Task HandleAsync(RestoreApprovalRegistry approvals, JobRunner jobRunner, string jobId)
    {
        if (approvals.HasPending(jobId)) { approvals.Resolve(jobId, null); return Task.CompletedTask; }

        jobRunner.CancelParked(jobId);   // mark cancelled + broadcast Done
        return Task.CompletedTask;
    }
}
