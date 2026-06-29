using System.CommandLine;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ChunkHydrationStatusQuery;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RepairChunkIndexCommand;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Features.StatisticsQuery;
using Arius.Core.Features.StorageAccountInfoQuery;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Arius.Cli.Tests.TestSupport;

/// <summary>
/// Builds a CLI invocation harness with mock command handlers.
/// The factory passed to <see cref="CliBuilder.BuildRootCommand"/> registers
/// <c>AddMediator()</c> then overrides all three handler interfaces with
/// NSubstitute mocks that capture the command objects for assertion.
/// </summary>
internal sealed class CliHarness
{
    public ICommandHandler<ArchiveCommand, ArchiveResult> ArchiveHandler { get; }
    public ICommandHandler<RestoreCommand, RestoreResult> RestoreHandler { get; }
    public ICommandHandler<RepairChunkIndexCommand, RepairChunkIndexResult> RepairHandler { get; }
    public IStreamQueryHandler<ListQuery, RepositoryEntry> ListQueryHandler { get; }
    public IStreamQueryHandler<ChunkHydrationStatusQuery, ChunkHydrationStatusResult> HydrationHandler { get; }

    public string? ResolvedAccount { get; private set; }

    public string? ResolvedKey { get; private set; }

    private readonly RootCommand _rootCommand;

    public CliHarness()
    {
        var archiveHandler = Substitute.For<ICommandHandler<ArchiveCommand, ArchiveResult>>();
        var restoreHandler = Substitute.For<ICommandHandler<RestoreCommand, RestoreResult>>();
        var repairHandler = Substitute.For<ICommandHandler<RepairChunkIndexCommand, RepairChunkIndexResult>>();
        var listQueryHandler = Substitute.For<IStreamQueryHandler<ListQuery, RepositoryEntry>>();
        var hydrationHandler = Substitute.For<IStreamQueryHandler<ChunkHydrationStatusQuery, ChunkHydrationStatusResult>>();
        // Mediator's source generator resolves every command handler when a command is sent, so the
        // CLI-unused snapshot/stats query handlers must be supplied too (they otherwise need a real
        // ISnapshotService the harness has no reason to wire up).
        var snapshotsHandler = Substitute.For<IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>>();
        var statsHandler = Substitute.For<IQueryHandler<StatisticsQuery, RepositoryStatistics>>();
        var storageInfoHandler = Substitute.For<IQueryHandler<StorageAccountInfoQuery, StorageAccountInfo>>();

        archiveHandler
            .Handle(Arg.Any<ArchiveCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult
            {
                Success               = true,
                FilesScanned          = 0,
                EntriesExcluded       = 0,
                FastHashReused        = 0,
                FastHashRehashed      = 0,
                FilesUploaded         = 0,
                FilesDeduped          = 0,
                OriginalSize          = 0,
                IncrementalSize       = 0,
                IncrementalStoredSize = 0,
                RootHash              = null,
                SnapshotTime          = DateTimeOffset.UtcNow,
            });

        restoreHandler
            .Handle(Arg.Any<RestoreCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RestoreResult
            {
                Success = true,
                FilesRestored = 0,
                FilesSkipped = 0,
                ChunksPendingRehydration = 0,
            });

        repairHandler
            .Handle(Arg.Any<RepairChunkIndexCommand>(), Arg.Any<CancellationToken>())
            .Returns(new RepairChunkIndexResult { Success = true, Repair = new(0, 0, 0, 0, 0) });

        listQueryHandler
            .Handle(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RepositoryEntry>());

        hydrationHandler
            .Handle(Arg.Any<ChunkHydrationStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<ChunkHydrationStatusResult>());

        snapshotsHandler
            .Handle(Arg.Any<SnapshotsListQuery>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<SnapshotInfo>());

        statsHandler
            .Handle(Arg.Any<StatisticsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<RepositoryStatistics>(new RepositoryStatistics(0, 0, 0, 0, 0, 0, [])));

        storageInfoHandler
            .Handle(Arg.Any<StorageAccountInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<StorageAccountInfo>(new StorageAccountInfo("test", RegionIsDefault: false)));

        ArchiveHandler = archiveHandler;
        RestoreHandler = restoreHandler;
        RepairHandler = repairHandler;
        ListQueryHandler = listQueryHandler;
        HydrationHandler = hydrationHandler;

        _rootCommand = CliBuilder.BuildRootCommand(serviceProviderFactory: (account, key, passphrase, container, _) =>
        {
            ResolvedAccount = account;
            ResolvedKey = key;

            var services = new ServiceCollection();
            services.AddMediator();
            services.AddSingleton<ProgressState>();
            services.AddSingleton(archiveHandler);
            services.AddSingleton(restoreHandler);
            services.AddSingleton(repairHandler);
            services.AddSingleton(listQueryHandler);
            services.AddSingleton(hydrationHandler);
            services.AddSingleton(snapshotsHandler);
            services.AddSingleton(statsHandler);
            services.AddSingleton(storageInfoHandler);
            return Task.FromResult<IServiceProvider>(services.BuildServiceProvider());
        });
    }

    public async Task<int> InvokeAsync(string args) => await _rootCommand.Parse(args).InvokeAsync();
}
