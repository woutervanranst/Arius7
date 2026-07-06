using System.Collections.Concurrent;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Jobs;

/// <summary>
/// Parks a restore's <c>ConfirmRehydration</c> callback until the client answers the cost modal
/// (<c>JobsHub.ApproveRestore</c>/<c>DeclineRestore</c>) or the run's token is cancelled. Keyed by jobId, so ANY
/// connection may answer. There is no in-process timeout: an unanswered prompt keeps the run live (holding its
/// read provider) until answered, until the run's token is cancelled (process shutdown), or until the
/// out-of-band <see cref="StaleApprovalSweepService"/> declines it after 24h. <c>null</c> = decline.
/// </summary>
public sealed class RestoreApprovalRegistry
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RehydratePriority?>> _pending = new();

    /// <summary>Awaited by the restore command. Completes with the approved priority, or <c>null</c> on decline.
    /// Throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> is cancelled. Always removes its
    /// own pending entry.</summary>
    public async Task<RehydratePriority?> RegisterAsync(string jobId, CancellationToken ct)
    {
        var tcs = _pending.GetOrAdd(jobId, _ => new TaskCompletionSource<RehydratePriority?>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(jobId, out _);
        }
    }

    /// <summary>Completes the pending approval for a job (priority to proceed, or <c>null</c> to decline). No-op
    /// if nothing is waiting.</summary>
    public void Resolve(string jobId, RehydratePriority? priority)
    {
        if (_pending.TryGetValue(jobId, out var tcs))
            tcs.TrySetResult(priority);
    }

    /// <summary>Whether a live wait is currently parked for this job.</summary>
    public bool HasPending(string jobId) => _pending.ContainsKey(jobId);
}
