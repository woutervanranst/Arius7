using Arius.Core.Features.LatestSnapshotVersionQuery;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Features.StatisticsQuery;
using Mediator;

namespace Arius.Api.FakeTestHost;

/// <summary>Fake read side: answers the browse, snapshot-list and statistics queries from the repository's
/// <see cref="ReadScenario"/>, recording each query it receives. No storage is touched.</summary>
public sealed class ScriptedReadHandler(ReadScenario scenario)
    : IStreamQueryHandler<ListQuery, RepositoryEntry>,
      IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>,
      IQueryHandler<LatestSnapshotVersionQuery, string?>,
      IQueryHandler<StatisticsQuery, RepositoryStatistics>
{
    public IAsyncEnumerable<RepositoryEntry> Handle(ListQuery query, CancellationToken cancellationToken)
    {
        scenario.Received.Enqueue(query);
        return (scenario.Entries ?? []).ToAsyncEnumerable();
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
