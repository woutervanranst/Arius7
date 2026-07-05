using System.Collections.Concurrent;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Mediator;

namespace Arius.Api.Integration.Tests.Harness;

/// <summary>A scripted archive run: publish <paramref name="Events"/> in order, then return <paramref name="Result"/>.</summary>
public sealed record ArchiveScenario(IReadOnlyList<INotification> Events, ArchiveResult Result);

/// <summary>
/// A scripted restore run: publish <paramref name="PreCostEvents"/>; if <paramref name="CostPrompt"/> is set,
/// invoke the run's ConfirmRehydration callback with it (declined/timed-out ⇒ stop, return <paramref name="Result"/>);
/// otherwise publish <paramref name="PostApproveEvents"/> and return <paramref name="Result"/>.
/// </summary>
public sealed record RestoreScenario(
    IReadOnlyList<INotification> PreCostEvents,
    RestoreCostEstimate? CostPrompt,
    IReadOnlyList<INotification> PostApproveEvents,
    RestoreResult Result);

/// <summary>Holds the next scripted scenario for each repository. Set by tests, taken by the scripted handlers.</summary>
public sealed class ScenarioRegistry
{
    private readonly ConcurrentDictionary<long, ArchiveScenario> _archive = new();
    private readonly ConcurrentDictionary<long, RestoreScenario> _restore = new();

    public void SetArchive(long repositoryId, ArchiveScenario scenario) => _archive[repositoryId] = scenario;
    public void SetRestore(long repositoryId, RestoreScenario scenario) => _restore[repositoryId] = scenario;

    public ArchiveScenario? TakeArchive(long repositoryId) => _archive.TryGetValue(repositoryId, out var s) ? s : null;
    public RestoreScenario? TakeRestore(long repositoryId) => _restore.TryGetValue(repositoryId, out var s) ? s : null;
}
