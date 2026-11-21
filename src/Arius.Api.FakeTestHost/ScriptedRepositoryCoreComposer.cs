using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ChunkHydrationStatusQuery;
using Arius.Core.Features.ContainerNamesQuery;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RepairChunkIndexCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Features.SnapshotDiffQuery;
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Features.StatisticsQuery;
using Arius.Core.Features.StorageAccountInfoQuery;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;
using Mediator;

namespace Arius.Api.FakeTestHost;

/// <summary>Test composer: registers a scripted Core (scenario-driven handlers and a deterministic cost estimator).
/// Never opens a real container.</summary>
public sealed class ScriptedRepositoryCoreComposer(ScenarioRegistry scenarios, ScenarioGate gate) : IRepositoryCoreComposer
{
    public Task ComposeAsync(IServiceCollection services, RepositoryConnection connection, PreflightMode mode, CancellationToken cancellationToken)
    {
        var read = scenarios.TakeRead(connection.RepositoryId);
        if (read is { ContainerMissing: true })
            throw new PreflightException(PreflightErrorKind.ContainerNotFound, "scripted", connection.AccountName, connection.Container, statusCode: 404);

        services.AddSingleton<IStorageCostEstimator>(new ScriptedStorageCostEstimator());
        // Lets the scripted handlers hold a run until a control endpoint releases it.
        services.AddSingleton(gate);
        services.AddSingleton(new ScenarioContext(connection.RepositoryId));

        AddNotConfiguredStandIns(services);

        // Registered after the stand-ins so they win: Microsoft DI resolves the last registration of a service type.
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

        if (read is not null)
        {
            services.AddSingleton(read);
            services.AddSingleton<IStreamQueryHandler<ListQuery, RepositoryEntry>, ScriptedReadHandler>();
            services.AddSingleton<IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>, ScriptedReadHandler>();
            services.AddSingleton<IQueryHandler<StatisticsQuery, RepositoryStatistics>, ScriptedReadHandler>();
        }

        return Task.CompletedTask;
    }

    public void ComposeWithoutRepository(IServiceCollection services) =>
        services.AddSingleton<IStreamQueryHandler<ContainerNamesQuery, string>, ScriptedContainerNamesHandler>();

    private static void AddNotConfiguredStandIns(IServiceCollection services)
    {
        services.AddSingleton<ICommandHandler<ArchiveCommand, ArchiveResult>, NotConfiguredCommandHandler<ArchiveCommand, ArchiveResult>>();
        services.AddSingleton<ICommandHandler<RepairChunkIndexCommand, RepairChunkIndexResult>, NotConfiguredCommandHandler<RepairChunkIndexCommand, RepairChunkIndexResult>>();
        services.AddSingleton<ICommandHandler<RestoreCommand, RestoreResult>, NotConfiguredCommandHandler<RestoreCommand, RestoreResult>>();
        services.AddSingleton<IStreamQueryHandler<ChunkHydrationStatusQuery, ChunkHydrationStatusResult>, NotConfiguredStreamQueryHandler<ChunkHydrationStatusQuery, ChunkHydrationStatusResult>>();
        services.AddSingleton<IStreamQueryHandler<ListQuery, RepositoryEntry>, NotConfiguredStreamQueryHandler<ListQuery, RepositoryEntry>>();
        services.AddSingleton<IStreamQueryHandler<SnapshotDiffQuery, SnapshotDiffEntry>, NotConfiguredStreamQueryHandler<SnapshotDiffQuery, SnapshotDiffEntry>>();
        services.AddSingleton<IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>, NotConfiguredStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>>();
        services.AddSingleton<IQueryHandler<StatisticsQuery, RepositoryStatistics>, NotConfiguredQueryHandler<StatisticsQuery, RepositoryStatistics>>();
        services.AddSingleton<IQueryHandler<StorageAccountInfoQuery, StorageAccountInfo>, NotConfiguredQueryHandler<StorageAccountInfoQuery, StorageAccountInfo>>();
        // ContainerNamesQuery gets no stand-in: its handler needs no repository services.
    }
}
