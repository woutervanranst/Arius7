using System.CommandLine;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RepairChunkIndexCommand;
using Arius.Core.Features.RestoreCommand;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Arius.Cli.Tests.TestSupport;

/// <summary>
/// Invokes the CLI against a service provider whose Core handlers are NSubstitute mocks, so tests can assert the
/// commands and queries the CLI sends.
/// </summary>
internal sealed class CliHarness
{
    public ICommandHandler<ArchiveCommand, ArchiveResult> ArchiveHandler { get; }
    public ICommandHandler<RestoreCommand, RestoreResult> RestoreHandler { get; }
    public ICommandHandler<RepairChunkIndexCommand, RepairChunkIndexResult> RepairHandler { get; }
    public IStreamQueryHandler<ListQuery, RepositoryEntry> ListQueryHandler { get; }

    public string? ResolvedAccount { get; private set; }

    public string? ResolvedKey { get; private set; }

    private readonly RootCommand _rootCommand;

    public CliHarness()
    {
        var archiveHandler = Substitute.For<ICommandHandler<ArchiveCommand, ArchiveResult>>();
        var restoreHandler = Substitute.For<ICommandHandler<RestoreCommand, RestoreResult>>();
        var repairHandler = Substitute.For<ICommandHandler<RepairChunkIndexCommand, RepairChunkIndexResult>>();
        var listQueryHandler = Substitute.For<IStreamQueryHandler<ListQuery, RepositoryEntry>>();

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

        ArchiveHandler = archiveHandler;
        RestoreHandler = restoreHandler;
        RepairHandler = repairHandler;
        ListQueryHandler = listQueryHandler;

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
            return Task.FromResult<IServiceProvider>(services.BuildServiceProvider());
        });
    }

    public async Task<int> InvokeAsync(string args) => await _rootCommand.Parse(args).InvokeAsync();
}
