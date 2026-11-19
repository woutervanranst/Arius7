using System.Runtime.CompilerServices;
using Arius.Core.Features.LatestSnapshotVersionQuery;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Features.StatisticsQuery;
using Mediator;

namespace Arius.Api.FakeTestHost;

/// <summary>Fake read side: answers the read queries from the repository's <see cref="ReadScenario"/> and records
/// each query it receives. No storage is touched.</summary>
public sealed class ScriptedReadHandler(ReadScenario scenario, ScenarioGate gate, ScenarioContext ctx)
    : IStreamQueryHandler<ListQuery, RepositoryEntry>,
      IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>,
      IQueryHandler<LatestSnapshotVersionQuery, string?>,
      IQueryHandler<StatisticsQuery, RepositoryStatistics>
{
    public async IAsyncEnumerable<RepositoryEntry> Handle(ListQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        scenario.Received.Enqueue(query);
        foreach (var entry in scenario.Entries ?? [])
            yield return entry;

        if (scenario.Gated)
            await gate.WaitForRelease(ctx.RepositoryId, cancellationToken);   // keeps the listing open until released
    }

    public IAsyncEnumerable<SnapshotInfo> Handle(SnapshotsListQuery query, CancellationToken cancellationToken)
    {
        scenario.Received.Enqueue(query);
        return (scenario.Snapshots ?? []).ToAsyncEnumerable();
    }

    public ValueTask<string?> Handle(LatestSnapshotVersionQuery query, CancellationToken cancellationToken)
    {
        scenario.Received.Enqueue(query);
        return ValueTask.FromResult(scenario.LatestVersion);
    }

    public ValueTask<RepositoryStatistics> Handle(StatisticsQuery query, CancellationToken cancellationToken)
    {
        scenario.Received.Enqueue(query);
        return ValueTask.FromResult(scenario.Statistics
            ?? throw new NotSupportedException("Scripted test harness: the read scenario scripts no statistics."));
    }
}
