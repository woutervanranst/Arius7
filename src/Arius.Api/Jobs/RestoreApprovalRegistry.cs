using System.Collections.Concurrent;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Jobs;

/// <summary>
/// Parks a restore's <c>ConfirmRehydration</c> callback on a <see cref="TaskCompletionSource{T}"/>
/// until the client answers the cost modal via <c>JobsHub.Approve</c>. <c>null</c> = decline/cancel.
/// A pending approval is also declined when its owning connection drops (closed tab / lost socket),
/// so a never-answered modal can't park the restore — and the per-repo write lock — indefinitely.
/// </summary>
public sealed class RestoreApprovalRegistry
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RehydratePriority?>> _pending = new();
    private readonly ConcurrentDictionary<string, string> _ownerByJob = new();   // jobId → connectionId

    /// <summary>Awaited by the restore command; completes when the client approves or declines.</summary>
    public Task<RehydratePriority?> Register(string jobId)
        => _pending.GetOrAdd(jobId, _ => new TaskCompletionSource<RehydratePriority?>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    /// <summary>Records which client connection owns a restore job's cost modal.</summary>
    public void Track(string jobId, string connectionId) => _ownerByJob[jobId] = connectionId;

    /// <summary>Completes the pending approval for a job (priority, or null to decline).</summary>
    public void Resolve(string jobId, RehydratePriority? priority)
    {
        _ownerByJob.TryRemove(jobId, out _);
        if (_pending.TryRemove(jobId, out var tcs))
            tcs.TrySetResult(priority);
    }

    /// <summary>Declines every pending approval owned by a disconnected connection (treats it as cancel).</summary>
    public void CancelForConnection(string connectionId)
    {
        foreach (var (jobId, owner) in _ownerByJob)
            if (owner == connectionId)
                Resolve(jobId, null);
    }
}
