using Arius.Core.Shared.ChunkIndex;

namespace Arius.Core.Shared.Cost;

/// <summary>
/// Cloud-agnostic storage cost estimation. The core depends only on this abstraction; each cloud provider
/// (e.g. Arius.AzureBlob) supplies an implementation that owns its own pricing data and rate model. The
/// canonical inputs/outputs (per-tier stored sizes via <see cref="ChunkTierStatistic"/>, region names,
/// <see cref="StorageCostEstimate"/>, <see cref="RestoreCostEstimate"/>) are provider-neutral.
/// </summary>
public interface IStorageCostEstimator
{
    /// <summary>Programmatic region identifiers this provider has pricing for (for the account-region dropdown).</summary>
    IReadOnlyList<string> Regions { get; }

    /// <summary>
    /// Estimates the monthly storage cost per tier for <paramref name="region"/> (a null/"Unknown"/unknown
    /// region falls back to the provider's default region).
    /// </summary>
    StorageCostEstimate EstimateStorageCost(string? region, IReadOnlyList<ChunkTierStatistic> storedByTier);

    /// <summary>
    /// Estimates the cost to restore the chunk set described by <paramref name="request"/> in
    /// <paramref name="region"/> (download/retrieval + any rehydration + egress).
    /// </summary>
    RestoreCostEstimate EstimateRestoreCost(string? region, RestoreCostRequest request);
}
