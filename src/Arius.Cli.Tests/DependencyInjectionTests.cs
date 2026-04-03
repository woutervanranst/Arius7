using Arius.Core;
using Arius.Core.Features.ArchiveCommand;
using Arius.Core.Features.ChunkHydrationStatusQuery;
using Arius.Core.Features.ContainerNamesQuery;
using Arius.Core.Features.ListQuery;
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
        services.AddSingleton(Substitute.For<IBlobServiceFactory>());

        using var serviceProvider = services.BuildServiceProvider();

        serviceProvider.GetRequiredService<IStreamQueryHandler<ContainerNamesQuery, string>>().ShouldBeOfType<ContainerNamesQueryHandler>();
        serviceProvider.GetRequiredService<IStreamQueryHandler<ListQuery, RepositoryEntry>>().ShouldBeOfType<ListQueryHandler>();
        serviceProvider.GetRequiredService<IStreamQueryHandler<ChunkHydrationStatusQuery, ChunkHydrationStatusResult>>().ShouldBeOfType<ChunkHydrationStatusQueryHandler>();
        serviceProvider.GetRequiredService<ICommandHandler<ArchiveCommand, ArchiveResult>>().ShouldBeOfType<ArchiveCommandHandler>();
        serviceProvider.GetRequiredService<ICommandHandler<RestoreCommand, RestoreResult>>().ShouldBeOfType<RestoreCommandHandler>();
    }
}
