using System.Collections.Concurrent;

namespace Arius.Api.Testing;

/// <summary>Per-repository release latch so a scripted run can be held mid-flight (e.g. an archive kept
/// "running" so a browser test can observe it in the Active list) until a control endpoint releases it.
/// A repo with no gated scenario never awaits it.</summary>
public sealed class ScenarioGate
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _gates = new();

    public Task WaitForRelease(long repositoryId, CancellationToken ct)
    {
        var tcs = _gates.GetOrAdd(repositoryId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        return tcs.Task.WaitAsync(ct);
    }

    public void Release(long repositoryId)
    {
        if (_gates.TryGetValue(repositoryId, out var tcs)) tcs.TrySetResult();
    }

    public void ReleaseAll()
    {
        foreach (var tcs in _gates.Values) tcs.TrySetResult();
        _gates.Clear();
    }
}

/// <summary>The repository id of the per-repo scripted provider, so scripted handlers can key the gate.</summary>
public sealed record ScenarioContext(long RepositoryId);
