using System.Collections.Concurrent;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Jobs;

/// <summary>
/// Parks a restore's <c>ConfirmRehydration</c> callback on a <see cref="TaskCompletionSource{T}"/>
/// until the client answers the cost modal via <c>JobsHub.Approve</c>. <c>null</c> = decline/cancel.
/// </summary>
public sealed class RestoreApprovalRegistry
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RehydratePriority?>> _pending = new();

    /// <summary>Awaited by the restore command; completes when the client approves or declines.</summary>
    public Task<RehydratePriority?> Register(string jobId)
        => _pending.GetOrAdd(jobId, _ => new TaskCompletionSource<RehydratePriority?>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    /// <summary>Completes the pending approval for a job (priority, or null to decline).</summary>
    public void Resolve(string jobId, RehydratePriority? priority)
    {
        if (_pending.TryRemove(jobId, out var tcs))
            tcs.TrySetResult(priority);
    }
}
