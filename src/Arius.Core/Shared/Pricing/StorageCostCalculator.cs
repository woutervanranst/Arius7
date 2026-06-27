using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.Pricing;

/// <summary>
/// Computes the estimated monthly storage cost per tier from stored sizes and the region's published
/// storage rates. Region-aware sibling of <c>RestoreCostCalculator</c> (which stays restore-specific);
/// both read rates from the shared <see cref="PricingCatalog"/>.
/// </summary>
public sealed class StorageCostCalculator(PricingCatalog? catalog = null)
{
    // Match RestoreCostCalculator: GB means GiB (1024^3 bytes).
    private const double BytesPerGiB = 1024.0 * 1024.0 * 1024.0;

    private readonly PricingCatalog _catalog = catalog ?? PricingCatalog.LoadEmbedded();

    /// <summary>
    /// Estimates the monthly storage cost for each tier (and the grand total) for a given region.
    /// The reported <see cref="StorageCostBreakdown.Region"/> is the region actually applied — the
    /// requested region when known, otherwise the catalog's default.
    /// </summary>
    public StorageCostBreakdown Compute(string? region, IReadOnlyList<ChunkTierStatistic> byTier)
    {
        var (resolvedRegion, pricing) = _catalog.Resolve(region);

        var tiers = byTier
            .Select(t => new TierStorageCost(
                t.Tier,
                t.UniqueChunks,
                t.StoredSize,
                CostPerMonth: t.StoredSize / BytesPerGiB * pricing.StorageRateFor(t.Tier)))
            .ToList();

        return new StorageCostBreakdown(resolvedRegion, pricing.Currency, tiers, tiers.Sum(t => t.CostPerMonth));
    }
}

/// <summary>Per-tier stored size + distinct-chunk count, paired with the estimated monthly storage cost.</summary>
public sealed record TierStorageCost(BlobTier Tier, long UniqueChunks, long StoredSize, double CostPerMonth);

/// <summary>Per-tier storage cost breakdown for one region, with the grand-total monthly cost.</summary>
public sealed record StorageCostBreakdown(
    string Region,
    string Currency,
    IReadOnlyList<TierStorageCost> Tiers,
    double TotalPerMonth);
