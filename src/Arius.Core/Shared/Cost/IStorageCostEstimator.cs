using Arius.Core.Shared.ChunkIndex;

namespace Arius.Core.Shared.Cost;

/// <summary>
/// Cloud-agnostic storage cost estimation. The core depends only on this abstraction; each cloud provider
/// (e.g. Arius.AzureBlob) supplies an implementation that owns its own pricing data and rate model. An
/// estimator is bound to a single repository's storage, so the region is an implementation detail (resolved
/// from the container) rather than a per-call parameter. The canonical inputs/outputs (per-tier stored sizes
/// via <see cref="ChunkTierStatistic"/>, <see cref="StorageCostEstimate"/>, <see cref="RestoreCostEstimate"/>)
/// are provider-neutral.
/// </summary>
public interface IStorageCostEstimator
{
    /// <summary>
    /// The region this estimator prices for: the container's configured region, or the provider's fallback
    /// default when unset. Bound once to the repository's storage; the default is the provider's own concern.
    /// </summary>
    string Region { get; }

    /// <summary>Estimates the monthly storage cost per tier for this repository's storage.</summary>
    StorageCostEstimate EstimateStorageCost(IReadOnlyList<ChunkTierStatistic> storedByTier);

    /// <summary>
    /// Estimates the cost to restore the chunk set described by <paramref name="request"/>
    /// (download/retrieval + any rehydration + egress).
    /// </summary>
    RestoreCostEstimate EstimateRestoreCost(RestoreCostRequest request);
}
