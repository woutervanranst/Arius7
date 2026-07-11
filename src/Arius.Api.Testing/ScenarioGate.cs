using System.Collections.Concurrent;

namespace Arius.Api.Testing;

/// <summary>Per-repository release latch so a scripted run can be held mid-flight (e.g. an archive kept
/// "running" so a browser test can observe it in the Active list) until a control endpoint releases it.
/// A repo with no gated scenario never awaits it. <see cref="Release"/> is a sticky latch: a release that
/// arrives before the run reaches <see cref="WaitForRelease"/> is remembered (create-or-complete), so both
/// orderings resolve. Latch state is cleared by <see cref="ReleaseAll"/> (test reset).</summary>
public sealed class ScenarioGate
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _gates = new();

    public Task WaitForRelease(long repositoryId, CancellationToken ct) => GateFor(repositoryId).Task.WaitAsync(ct);

    public void Release(long repositoryId)
    {
        // Create-or-complete: a release that arrives before the scripted handler reaches WaitForRelease must be
        // remembered, so the later WaitForRelease sees an already-completed latch instead of creating a fresh,
        // never-signalled TCS and hanging until the Playwright timeout. Sticky until ReleaseAll().
        GateFor(repositoryId).TrySetResult();
    }

    private TaskCompletionSource GateFor(long repositoryId) =>
        _gates.GetOrAdd(repositoryId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public void ReleaseAll()
    {
        foreach (var tcs in _gates.Values) tcs.TrySetResult();
        _gates.Clear();
    }
}

/// <summary>The repository id of the per-repo scripted provider, so scripted handlers can key the gate.</summary>
public sealed record ScenarioContext(long RepositoryId);
