using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Jobs;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Features.Restore;

/// <summary>Approves a restore's cost estimate. An <c>awaiting-cost</c> job always has a live run blocked in the
/// approval callback, so this only resolves that wait; after a restart such jobs are already <c>interrupted</c>.</summary>
public sealed class ApproveRestore(RestoreApprovalRegistry approvals) : IHubHandler
{
    public Task HandleAsync(string jobId, string? priority)
    {
        // An unknown or missing priority defaults to Standard (the cheaper tier), never null: the approval callback
        // reads null as a decline.
        var chosen = priority?.ToLowerInvariant() == "high" ? RehydratePriority.High : RehydratePriority.Standard;
        if (approvals.HasPending(jobId)) approvals.Resolve(jobId, chosen);
        return Task.CompletedTask;
    }
}
