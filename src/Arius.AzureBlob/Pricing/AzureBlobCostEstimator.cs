using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.Logging;

namespace Arius.AzureBlob.Pricing;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IStorageCostEstimator"/>. Owns the embedded region-keyed
/// pricing catalog and the Azure cost model (per-tier storage; restore = archive rehydration + online-tier
/// download retrieval/read-ops + internet egress). Azure bills per-GB values as binary GiB (2^30 bytes).
/// Bound to one repository's container: the region is resolved once from <see cref="IBlobContainerService.RegionHint"/>
/// (an unconfigured container falls back to <see cref="FallbackRegion"/> with a logged warning).
/// </summary>
public sealed class AzureBlobCostEstimator : IStorageCostEstimator
{
    private const double BytesPerGiB = 1024.0 * 1024.0 * 1024.0;

    /// <summary>Region used to price a container whose <c>region</c> metadata is not set.</summary>
    public const string FallbackRegion = "northeurope";

    private readonly string        _region;
    private readonly RegionPricing _pricing;

    public AzureBlobCostEstimator(IBlobContainerService container, ILogger<AzureBlobCostEstimator> logger)
        : this(AzurePricingCatalog.LoadEmbedded(), container.RegionHint, logger) { }

    internal AzureBlobCostEstimator(AzurePricingCatalog catalog, string? regionHint, ILogger<AzureBlobCostEstimator> logger)
    {
        if (string.IsNullOrEmpty(regionHint))
        {
            logger.LogWarning(
                "Container 'region' metadata is not set; pricing against {Fallback}. Set the container's 'region' " +
                "metadata (e.g. in Azure Storage Explorer) for accurate cost estimates.",
                FallbackRegion);
            regionHint = FallbackRegion;
        }

        (_region, _pricing) = catalog.Resolve(regionHint);
    }

    public StorageCostEstimate EstimateStorageCost(IReadOnlyList<ChunkTierStatistic> storedByTier)
    {
        var tiers = storedByTier
            .Select(t => new TierStorageCost(
                t.Tier, t.UniqueChunks, t.StoredSize,
                CostPerMonth: t.StoredSize / BytesPerGiB * _pricing.StorageRateFor(t.Tier)))
            .ToList();
        return new StorageCostEstimate(_region, tiers, tiers.Sum(t => t.CostPerMonth));
    }

    public RestoreCostEstimate EstimateRestoreCost(RestoreCostRequest request)
    {
        var pricing = _pricing;
        var cost = AzureRestoreCostCalculator.Compute(pricing, request); // rich Azure breakdown → slim canonical estimate

        return new RestoreCostEstimate
        {
            ChunksAvailable          = request.ChunksAvailable,
            ChunksAlreadyRehydrated  = request.ChunksAlreadyRehydrated,
            ChunksNeedingRehydration = request.ChunksNeedingRehydration,
            ChunksPendingRehydration = request.ChunksPendingRehydration,
            BytesNeedingRehydration  = request.BytesNeedingRehydration,
            BytesPendingRehydration  = request.BytesPendingRehydration,
            DownloadBytes            = request.DownloadBytes,
            TotalStandard            = cost.TotalStandard,
            TotalHigh                = cost.TotalHigh,
        };
    }
}
