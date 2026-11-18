using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Jobs;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Features.Restore;

/// <summary>Hub <c>ApproveRestore</c> — answers the restore cost modal. An <c>awaiting-cost</c> job is always LIVE
/// within a process lifetime (its run blocks in the approval callback), so this only ever resolves an in-run wait; a
/// restart reconciles any parked job to <c>interrupted</c> before it could reach here.</summary>
public sealed class ApproveRestore(RestoreApprovalRegistry approvals) : IHubHandler
{
    public Task HandleAsync(string jobId, string? priority)
    {
        // ApproveRestore is unambiguously "proceed" — DeclineRestore is the separate decline path. An unrecognized
        // or missing priority therefore defaults to Standard (the cheaper tier), never null: a null here would be
        // read by the run's ConfirmRehydration callback as a decline and silently cancel a restore the user meant
        // to approve.
        var chosen = priority?.ToLowerInvariant() == "high" ? RehydratePriority.High : RehydratePriority.Standard;
        if (approvals.HasPending(jobId)) approvals.Resolve(jobId, chosen);   // in-run
        // else: no live approval wait to answer (job already resumed/terminal) — nothing to do.
        return Task.CompletedTask;
    }
}
