using Arius.Core;
using Arius.Core.Features.Archive;
using Arius.Core.Features.Hydration;
using Arius.Core.Features.List;
using Arius.Core.Features.Restore;
using Arius.Core.Shared.Storage;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Arius.Cli.Tests;

public class AddAriusRegistrationTests
{
    [Test]
    public void AddArius_RegistersStreamingLsHandlerInterface()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddMediator();
        services.AddArius(
            blobContainer: Substitute.For<IBlobContainerService>(),
            passphrase: null,
            accountName: "test",
            containerName: "test",
            cacheBudgetBytes: 0);

        using var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetRequiredService<IStreamQueryHandler<ListRepositoryEntriesCommand, RepositoryEntry>>().ShouldBeOfType<ListRepositoryEntriesHandler>();
        serviceProvider.GetRequiredService<IStreamQueryHandler<ResolveFileHydrationStatusesCommand, FileHydrationStatusResult>>().ShouldBeOfType<ResolveFileHydrationStatusesHandler>();
        serviceProvider.GetRequiredService<ICommandHandler<ArchiveCommand, ArchiveResult>>().ShouldBeOfType<ArchivePipelineHandler>();
        serviceProvider.GetRequiredService<ICommandHandler<RestoreCommand, RestoreResult>>().ShouldBeOfType<RestorePipelineHandler>();
    }
}
