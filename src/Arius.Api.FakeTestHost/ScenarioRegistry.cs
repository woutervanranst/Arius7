using System.Collections.Concurrent;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Features.StatisticsQuery;
using Arius.Core.Shared.Cost;
using Mediator;

namespace Arius.Api.FakeTestHost;

/// <summary>A scripted archive run: publishes <paramref name="Events"/> in order, then returns <paramref name="Result"/>.
/// When <paramref name="Gated"/>, the handler waits after the events until <see cref="ScenarioGate"/> releases the
/// repository, so a test can observe the running job before it completes.</summary>
public sealed record ArchiveScenario(IReadOnlyList<INotification> Events, ArchiveResult Result, bool Gated = false);

/// <summary>
/// A scripted restore run: publishes <paramref name="PreCostEvents"/>, then passes <paramref name="CostPrompt"/>, if
/// set, to the run's ConfirmRehydration callback. A declined or timed-out prompt returns <paramref name="Result"/>;
/// otherwise the run publishes <paramref name="PostApproveEvents"/> and returns <paramref name="Result"/>. When
/// <paramref name="Gated"/>, the handler waits after the post-approve events until <see cref="ScenarioGate"/>
/// releases the repository. When <paramref name="WritesFiles"/>, the handler writes each restored file under the
/// command's root directory before publishing its <see cref="FileRestoredEvent"/>, as a download job needs.
/// </summary>
public sealed record RestoreScenario(
    IReadOnlyList<INotification> PreCostEvents,
    RestoreCostEstimate? CostPrompt,
    IReadOnlyList<INotification> PostApproveEvents,
    RestoreResult Result,
    bool Gated = false,
    bool WritesFiles = false)
{
    /// <summary>Every command the scripted handler received, in order, so a test can assert what the host sent Core.</summary>
    public ConcurrentQueue<RestoreCommand> Received { get; } = new();
}

/// <summary>
/// A scripted read side: what the listing (<see cref="ListQuery"/>), snapshot list, latest snapshot version and
/// statistics queries return. A <c>null</c> list streams nothing; <c>null</c> statistics throw. When
/// <paramref name="ContainerMissing"/>, building the provider fails as it does for a container that does not exist
/// yet (<see cref="Arius.Core.Shared.Storage.PreflightErrorKind.ContainerNotFound"/>). When <paramref name="Gated"/>,
/// the listing stays open after its entries until <see cref="ScenarioGate"/> releases the repository, so a test can
/// observe entries arriving while it runs.
/// </summary>
public sealed record ReadScenario(
    IReadOnlyList<RepositoryEntry>? Entries = null,
    IReadOnlyList<SnapshotInfo>? Snapshots = null,
    RepositoryStatistics? Statistics = null,
    string? LatestVersion = null,
    bool ContainerMissing = false,
    bool Gated = false)
{
    /// <summary>Every query the scripted read handler received, in order, so a test can assert what the host asked Core.</summary>
    public ConcurrentQueue<object> Received { get; } = new();
}

/// <summary>The scripted scenarios per repository: set by tests, read by <see cref="ScriptedRepositoryCoreComposer"/>.</summary>
public sealed class ScenarioRegistry
{
    private readonly ConcurrentDictionary<long, ArchiveScenario> _archive = new();
    private readonly ConcurrentDictionary<long, RestoreScenario> _restore = new();
    private readonly ConcurrentDictionary<long, ReadScenario> _read = new();

    public void SetArchive(long repositoryId, ArchiveScenario scenario) => _archive[repositoryId] = scenario;
    public void SetRestore(long repositoryId, RestoreScenario scenario) => _restore[repositoryId] = scenario;
    public void SetRead(long repositoryId, ReadScenario scenario) => _read[repositoryId] = scenario;

    public ArchiveScenario? TakeArchive(long repositoryId) => _archive.TryGetValue(repositoryId, out var s) ? s : null;
    public RestoreScenario? TakeRestore(long repositoryId) => _restore.TryGetValue(repositoryId, out var s) ? s : null;
    public ReadScenario? TakeRead(long repositoryId) => _read.TryGetValue(repositoryId, out var s) ? s : null;

    /// <summary>Clears all scenarios, so hermetic specs are isolated from each other.</summary>
    public void Clear() { _archive.Clear(); _restore.Clear(); _read.Clear(); }
}
