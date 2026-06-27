using Arius.AzureBlob.Pricing;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.AzureBlob;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Azure Blob Storage adapters — the blob-service factory and the cost estimator —
    /// as singletons. Call once per composition root, before <c>AddArius</c>, so the two Azure-specific
    /// services (<see cref="IBlobServiceFactory"/> and <see cref="IStorageCostEstimator"/>) are wired
    /// in one place rather than scattered across each host's startup.
    /// </summary>
    public static IServiceCollection AddAzureBlobStorage(this IServiceCollection services)
    {
        services.AddSingleton<IBlobServiceFactory, AzureBlobServiceFactory>();
        services.AddSingleton<IStorageCostEstimator, AzureBlobCostEstimator>();
        return services;
    }
}
