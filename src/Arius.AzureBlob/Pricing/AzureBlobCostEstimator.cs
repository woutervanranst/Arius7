using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;

namespace Arius.AzureBlob.Pricing;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IStorageCostEstimator"/>. Owns the embedded region-keyed
/// pricing catalog and the Azure cost model (per-tier storage; restore = archive rehydration + online-tier
/// download retrieval/read-ops + internet egress). Azure bills per-GB values as binary GiB (2^30 bytes).
/// </summary>
public sealed class AzureBlobCostEstimator : IStorageCostEstimator
{
    private const double BytesPerGiB = 1024.0 * 1024.0 * 1024.0;

    private readonly AzurePricingCatalog _catalog;

    public AzureBlobCostEstimator() : this(AzurePricingCatalog.LoadEmbedded()) { }

    internal AzureBlobCostEstimator(AzurePricingCatalog catalog) => _catalog = catalog;

    public IReadOnlyList<string> Regions => _catalog.RegionNames;

    public StorageCostEstimate EstimateStorageCost(string? region, IReadOnlyList<ChunkTierStatistic> storedByTier)
    {
        var (name, pricing) = _catalog.Resolve(region);
        var tiers = storedByTier
            .Select(t => new TierStorageCost(
                t.Tier, t.UniqueChunks, t.StoredSize,
                CostPerMonth: t.StoredSize / BytesPerGiB * pricing.StorageRateFor(t.Tier)))
            .ToList();
        return new StorageCostEstimate(name, tiers, tiers.Sum(t => t.CostPerMonth));
    }

    public RestoreCostEstimate EstimateRestoreCost(string? region, RestoreCostRequest request)
    {
        var (_, pricing) = _catalog.Resolve(region);
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
