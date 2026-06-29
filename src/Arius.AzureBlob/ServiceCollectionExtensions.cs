using Arius.AzureBlob.Pricing;
using Arius.Core.Shared.Cost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arius.AzureBlob;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Azure Blob Storage adapters — the blob-service factory and the cost estimator —
    /// as singletons. Call once per composition root, before <c>AddArius</c>, so the two Azure-specific
    /// services (<see cref="IBlobServiceFactory"/> and <see cref="IStorageCostEstimator"/>) are wired
    /// in one place rather than scattered across each host's startup.
    /// </summary>
    /// <remarks>
    /// The cost estimator is bound to the repository's <see cref="IBlobContainerService"/> (it reads the
    /// container's region), so it is only resolvable inside a per-repository provider. The factory
    /// registration (rather than open-generic activation) keeps that dependency lazy, so a host that registers
    /// this but never resolves the estimator — e.g. the API's root container — does not need a container.
    /// </remarks>
    public static IServiceCollection AddAzureBlobStorage(this IServiceCollection services)
    {
        services.AddSingleton<IBlobServiceFactory, AzureBlobServiceFactory>();
        services.AddSingleton<IStorageCostEstimator>(sp =>
            new AzureBlobCostEstimator(
                sp.GetRequiredService<IBlobContainerService>(),
                sp.GetRequiredService<ILogger<AzureBlobCostEstimator>>()));
        return services;
    }
}
