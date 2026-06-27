using Arius.Core;
using Arius.Core.Features.StorageAccountInfoQuery;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;
using Arius.Tests.Shared.Fakes;
using Arius.Tests.Shared.Storage;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arius.Cli.Tests;

/// <summary>
/// Verifies the source-generated Mediator actually dispatches the Core <see cref="StorageAccountInfoQuery"/>
/// to its handler — a handler the generator failed to wire would surface only at runtime (and the API swallows
/// the failure into a blank region) — and that the handler maps the resolved region and the "is default" flag
/// purely from Core abstractions.
/// </summary>
public class StorageAccountInfoQueryDispatchTests
{
    private static IServiceProvider BuildServices(IBlobContainerService container, IStorageCostEstimator estimator)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddSingleton<ProgressState>(); // the CLI's Mediator eagerly inits its notification handlers, which need this

        // AddMediator() must run in the outermost (test) assembly so the source generator discovers the
        // Core query handler — exactly the wiring this test guards.
        services.AddMediator();
        services.AddSingleton(estimator);
        services.AddArius(container, passphrase: null, accountName: "test", containerName: "test");

        return services.BuildServiceProvider();
    }

    [Test]
    public async Task ConfiguredRegion_IsReported_AndNotFlaggedDefault()
    {
        var container = new FakeInMemoryBlobContainerService { RegionHint = "westeurope" };
        var sp = BuildServices(container, new FakeStorageCostEstimator { Region = "westeurope" });

        var info = await sp.GetRequiredService<IMediator>().Send(new StorageAccountInfoQuery());

        info.Region.ShouldBe("westeurope");
        info.RegionIsDefault.ShouldBeFalse();
    }

    [Test]
    public async Task UnsetRegion_ReportsResolvedDefault_AndIsFlaggedDefault()
    {
        var container = new FakeInMemoryBlobContainerService(); // RegionHint null = container metadata unset
        // The estimator reports the provider's resolved fallback region; the handler flags it as default because
        // the container carries no region metadata.
        var sp = BuildServices(container, new FakeStorageCostEstimator { Region = "northeurope" });

        var info = await sp.GetRequiredService<IMediator>().Send(new StorageAccountInfoQuery());

        info.Region.ShouldBe("northeurope");
        info.RegionIsDefault.ShouldBeTrue();
    }
}
