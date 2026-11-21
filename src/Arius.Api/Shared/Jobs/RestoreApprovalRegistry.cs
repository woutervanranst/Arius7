using System.Collections.Concurrent;
using Arius.Api.Features.Restore;
using Arius.Api.Shared.Hubs;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Shared.Jobs;

/// <summary>
/// Holds a restore's <c>ConfirmRehydration</c> callback until a client answers the cost prompt or the run is
/// cancelled. Keyed by job id, so any connection may answer. There is no timeout here: an unanswered prompt keeps
/// the run alive until it is answered, the run is cancelled (e.g. on shutdown), or
/// <see cref="StaleApprovalSweepService"/> declines it after 24 hours. A <c>null</c> priority means decline.
/// </summary>
public sealed class RestoreApprovalRegistry
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RehydratePriority?>> _pending = new();

    /// <summary>Creates the job's approval waiter without awaiting it. Call it before the client can answer (before
    /// <c>JobSink.Cost</c>), so an answer that arrives before <see cref="RegisterAsync"/> awaits is not lost;
    /// <see cref="RegisterAsync"/> then awaits the same waiter.</summary>
    public void Prime(string jobId) =>
        _pending.GetOrAdd(jobId, _ => new TaskCompletionSource<RehydratePriority?>(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Waits for the answer: the approved priority, or <c>null</c> on decline. Throws
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled. Always removes the job's
    /// waiter.</summary>
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

    /// <summary>Answers the job's pending approval with a priority, or <c>null</c> to decline. No-op when nothing is
    /// waiting.</summary>
    public void Resolve(string jobId, RehydratePriority? priority)
    {
        if (_pending.TryGetValue(jobId, out var tcs))
            tcs.TrySetResult(priority);
    }

    public bool HasPending(string jobId) => _pending.ContainsKey(jobId);
}
