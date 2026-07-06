using System.Collections.Concurrent;
using Arius.Core.Shared.Storage;

namespace Arius.Api.Jobs;

/// <summary>Outcome of a cost-approval wait: approved with a priority, explicitly declined, or timed out
/// (an abandoned modal → the job parks at <c>awaiting-cost</c> and the repo gate is released; design §8).</summary>
public sealed record ApprovalResult(bool Approved, RehydratePriority? Priority, bool TimedOut);

/// <summary>
/// Parks a restore's <c>ConfirmRehydration</c> callback until the client answers the cost modal
/// (<c>JobsHub.ApproveRestore</c>/<c>DeclineRestore</c>) or a bounded timeout elapses. Keyed by jobId, so ANY
/// connection may answer (a closed tab no longer declines — the owner map and <c>CancelForConnection</c> of the
/// pre-rework design are gone). <c>null</c> = decline.
/// </summary>
public sealed class RestoreApprovalRegistry
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RehydratePriority?>> _pending = new();

    /// <summary>Awaited by the restore command. Completes when the client approves/declines, or reports a timeout
    /// after <paramref name="timeout"/> (or if <paramref name="ct"/> is cancelled — treated as a timeout so the
    /// caller parks rather than crashing). Always removes its own pending entry.</summary>
    public async Task<ApprovalResult> RegisterAsync(string jobId, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = _pending.GetOrAdd(jobId, _ => new TaskCompletionSource<RehydratePriority?>(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (completed != tcs.Task)
            {
                // Timeout fired — but a concurrent Resolve may have completed the TCS between WhenAny returning
                // and now. Atomically claim the timeout; if the claim FAILS, a real answer landed first, so honor
                // it — otherwise a genuine approval that raced the deadline would be silently discarded (#3).
                if (tcs.TrySetResult(null))
                    return new ApprovalResult(Approved: false, Priority: null, TimedOut: true);
                var raced = await tcs.Task.ConfigureAwait(false);
                return new ApprovalResult(Approved: raced is not null, Priority: raced, TimedOut: false);
            }

            var priority = await tcs.Task.ConfigureAwait(false);
            return new ApprovalResult(Approved: priority is not null, Priority: priority, TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            return new ApprovalResult(Approved: false, Priority: null, TimedOut: true);
        }
        finally
        {
            _pending.TryRemove(jobId, out _);
        }
    }

    /// <summary>Completes the pending approval for a job (priority to proceed, or <c>null</c> to decline). No-op
    /// if nothing is waiting (e.g. the wait already timed out, or the run is parked after a restart — the caller
    /// then routes to the re-trigger fallback).</summary>
    public void Resolve(string jobId, RehydratePriority? priority)
    {
        if (_pending.TryGetValue(jobId, out var tcs))
            tcs.TrySetResult(priority);
    }

    /// <summary>Whether a live wait is currently parked for this job (lets the hub choose in-run resolve vs.
    /// the restart/late-answer re-trigger fallback).</summary>
    public bool HasPending(string jobId) => _pending.ContainsKey(jobId);
}
