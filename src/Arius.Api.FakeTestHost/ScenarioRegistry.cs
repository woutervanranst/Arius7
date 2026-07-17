using System.Collections.Concurrent;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Mediator;

namespace Arius.Api.FakeTestHost;

/// <summary>A scripted archive run: publish <paramref name="Events"/> in order, then return <paramref name="Result"/>.
/// When <paramref name="Gated"/> the handler holds after the events until <see cref="ScenarioGate"/> releases the
/// repo (lets a browser test observe the job "running" in the Active list before it completes).</summary>
public sealed record ArchiveScenario(IReadOnlyList<INotification> Events, ArchiveResult Result, bool Gated = false);

/// <summary>
/// A scripted restore run: publish <paramref name="PreCostEvents"/>; if <paramref name="CostPrompt"/> is set,
/// invoke the run's ConfirmRehydration callback with it (declined/timed-out ⇒ stop, return <paramref name="Result"/>);
/// otherwise publish <paramref name="PostApproveEvents"/> and return <paramref name="Result"/>. When
/// <paramref name="Gated"/> the handler holds after the post-approve events until <see cref="ScenarioGate"/> releases.
/// </summary>
public sealed record RestoreScenario(
    IReadOnlyList<INotification> PreCostEvents,
    RestoreCostEstimate? CostPrompt,
    IReadOnlyList<INotification> PostApproveEvents,
    RestoreResult Result,
    bool Gated = false);

/// <summary>Holds the next scripted scenario for each repository. Set by tests, taken by the scripted handlers.</summary>
public sealed class ScenarioRegistry
{
    private readonly ConcurrentDictionary<long, ArchiveScenario> _archive = new();
    private readonly ConcurrentDictionary<long, RestoreScenario> _restore = new();

    public void SetArchive(long repositoryId, ArchiveScenario scenario) => _archive[repositoryId] = scenario;
    public void SetRestore(long repositoryId, RestoreScenario scenario) => _restore[repositoryId] = scenario;

    public ArchiveScenario? TakeArchive(long repositoryId) => _archive.TryGetValue(repositoryId, out var s) ? s : null;
    public RestoreScenario? TakeRestore(long repositoryId) => _restore.TryGetValue(repositoryId, out var s) ? s : null;

    /// <summary>Test-only: clear all registered scenarios (cross-spec isolation in the hermetic suite).</summary>
    public void Clear() { _archive.Clear(); _restore.Clear(); }
}
