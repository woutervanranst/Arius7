using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ChunkHydrationStatusQuery;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RepairChunkIndexCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Features.SnapshotDiffQuery;
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Features.StatisticsQuery;
using Arius.Core.Features.StorageAccountInfoQuery;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Fakes;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests.Harness;

/// <summary>Test composer: registers a scripted Core (scenario-driven command handlers + a deterministic
/// cost estimator) into the per-repo provider. Never opens a real container.</summary>
public sealed class ScriptedRepositoryCoreComposer(ScenarioRegistry scenarios) : IRepositoryCoreComposer
{
    public Task ComposeAsync(IServiceCollection services, RepositoryConnection connection, PreflightMode mode, CancellationToken cancellationToken)
    {
        services.AddSingleton<IStorageCostEstimator>(new FakeStorageCostEstimator());

        // Othamar Mediator's generated ContainerMetadata eagerly resolves EVERY discovered
        // command/query handler on the first Send/Publish through this provider's IMediator — not just
        // the one a given scenario scripts (see NotConfiguredHandlers.cs for why). Register a
        // NotConfigured stand-in for everything up front; the scenario-driven registrations below are
        // added after, so they win for whatever this test actually scripts.
        services.AddSingleton<ICommandHandler<RepairChunkIndexCommand, RepairChunkIndexResult>, NotConfiguredCommandHandler<RepairChunkIndexCommand, RepairChunkIndexResult>>();
        services.AddSingleton<ICommandHandler<RestoreCommand, RestoreResult>, NotConfiguredCommandHandler<RestoreCommand, RestoreResult>>();
        services.AddSingleton<IStreamQueryHandler<ChunkHydrationStatusQuery, ChunkHydrationStatusResult>, NotConfiguredStreamQueryHandler<ChunkHydrationStatusQuery, ChunkHydrationStatusResult>>();
        services.AddSingleton<IStreamQueryHandler<ListQuery, RepositoryEntry>, NotConfiguredStreamQueryHandler<ListQuery, RepositoryEntry>>();
        services.AddSingleton<IStreamQueryHandler<SnapshotDiffQuery, SnapshotDiffEntry>, NotConfiguredStreamQueryHandler<SnapshotDiffQuery, SnapshotDiffEntry>>();
        services.AddSingleton<IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>, NotConfiguredStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>>();
        services.AddSingleton<IQueryHandler<StatisticsQuery, RepositoryStatistics>, NotConfiguredQueryHandler<StatisticsQuery, RepositoryStatistics>>();
        services.AddSingleton<IQueryHandler<StorageAccountInfoQuery, StorageAccountInfo>, NotConfiguredQueryHandler<StorageAccountInfoQuery, StorageAccountInfo>>();
        // ContainerNamesQueryHandler(IServiceProvider) needs no Core services, so its real (AddMediator-
        // registered) handler resolves fine as-is — no stand-in required.

        var archive = scenarios.TakeArchive(connection.RepositoryId);
        if (archive is not null)
        {
            services.AddSingleton(archive);
            services.AddSingleton<ICommandHandler<ArchiveCommand, ArchiveResult>, ScriptedArchiveHandler>();
        }

        // Task 8 re-enables this once ScriptedRestoreHandler exists.
        //var restore = scenarios.TakeRestore(connection.RepositoryId);
        //if (restore is not null)
        //{
        //    services.AddSingleton(restore);
        //    services.AddSingleton<ICommandHandler<RestoreCommand, RestoreResult>, ScriptedRestoreHandler>();
        //}

        return Task.CompletedTask;
    }
}
