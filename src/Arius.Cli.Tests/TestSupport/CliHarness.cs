using System.CommandLine;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ChunkHydrationStatusQuery;
using Arius.Core.Features.ListQuery;
using Arius.Core.Features.RestoreCommand;
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
    public IStreamQueryHandler<ListQuery, RepositoryEntry> ListQueryHandler { get; }
    public IStreamQueryHandler<ChunkHydrationStatusQuery, ChunkHydrationStatusResult> HydrationHandler { get; }

    public string? ResolvedAccount { get; private set; }

    public string? ResolvedKey { get; private set; }

    private readonly RootCommand _rootCommand;

    public CliHarness()
    {
        var archiveHandler = Substitute.For<ICommandHandler<ArchiveCommand, ArchiveResult>>();
        var restoreHandler = Substitute.For<ICommandHandler<RestoreCommand, RestoreResult>>();
        var listQueryHandler = Substitute.For<IStreamQueryHandler<ListQuery, RepositoryEntry>>();
        var hydrationHandler = Substitute.For<IStreamQueryHandler<ChunkHydrationStatusQuery, ChunkHydrationStatusResult>>();

        archiveHandler
            .Handle(Arg.Any<ArchiveCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveResult
            {
                Success = true,
                FilesScanned = 0,
                FilesUploaded = 0,
                FilesDeduped = 0,
                TotalSize = 0,
                RootHash = null,
                SnapshotTime = DateTimeOffset.UtcNow,
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

        listQueryHandler
            .Handle(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<RepositoryEntry>());

        hydrationHandler
            .Handle(Arg.Any<ChunkHydrationStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<ChunkHydrationStatusResult>());

        ArchiveHandler = archiveHandler;
        RestoreHandler = restoreHandler;
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
            services.AddSingleton(listQueryHandler);
            services.AddSingleton(hydrationHandler);
            return Task.FromResult<IServiceProvider>(services.BuildServiceProvider());
        });
    }

    public async Task<int> InvokeAsync(string args) => await _rootCommand.Parse(args).InvokeAsync();
}
