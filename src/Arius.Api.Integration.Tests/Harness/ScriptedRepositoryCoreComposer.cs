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
        // NotConfigured stand-in for EVERY command/query/stream-query interface Arius.Core exposes up
        // front — this list must track the handler set AddArius() wires in
        // src/Arius.Core/ServiceCollectionExtensions.cs — including ArchiveCommand and RestoreCommand
        // (both are scenario-scriptable, so both need a baseline stand-in too). The scenario-driven
        // overrides below are registered AFTER, so — with MS-DI's last-registration-wins resolution —
        // they take over for whatever this test actually scripts, while every other handler (and either
        // of Archive/Restore when a test scripts only one of them) safely falls back to the stand-in
        // instead of eagerly resolving the real, un-constructable Core handler.
        services.AddSingleton<ICommandHandler<ArchiveCommand, ArchiveResult>, NotConfiguredCommandHandler<ArchiveCommand, ArchiveResult>>();
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

        // Scenario overrides — registered after the stand-ins above, so they win (MS-DI resolves the LAST
        // registration for a given service type). A test that sets only one of Archive/Restore leaves the
        // other on its stand-in, which is exactly what makes a restore-only (or archive-only) scenario safe.
        var archive = scenarios.TakeArchive(connection.RepositoryId);
        if (archive is not null)
        {
            services.AddSingleton(archive);
            services.AddSingleton<ICommandHandler<ArchiveCommand, ArchiveResult>, ScriptedArchiveHandler>();
        }

        var restore = scenarios.TakeRestore(connection.RepositoryId);
        if (restore is not null)
        {
            services.AddSingleton(restore);
            services.AddSingleton<ICommandHandler<RestoreCommand, RestoreResult>, ScriptedRestoreHandler>();
        }

        return Task.CompletedTask;
    }
}
