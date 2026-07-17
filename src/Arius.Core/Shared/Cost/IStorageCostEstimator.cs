using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.Cost;

/// <summary>
/// Cloud-agnostic storage cost estimation.
/// The canonical inputs/outputs (per-tier stored sizes via <see cref="ChunkTierStatistic"/>, <see cref="StorageCostEstimate"/>, <see cref="RestoreCostEstimate"/>) are provider-neutral.
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


// ── Storage cost ─────────────────────────────────────────────

/// <summary>Per-tier monthly storage cost breakdown for one region (amounts in EUR).</summary>
public sealed record StorageCostEstimate(string Region, IReadOnlyList<TierStorageCost> Tiers, double TotalPerMonth);

/// <summary>Stored size + distinct-chunk count for one tier, with its estimated monthly storage cost.</summary>
public sealed record TierStorageCost(BlobTier Tier, long UniqueChunks, long StoredSize, double CostPerMonth);


// ── Restore cost ──────────────────────────────────────────────────────────────

/// <summary>
/// What is being restored, as Arius classifies it from the chunk index — provider-neutral inputs to a
/// restore cost estimate. Online chunks to download are split by source tier (an already-rehydrated archive
/// copy counts as Hot); archive chunks are split into those needing a new rehydration request vs. already pending.
/// </summary>
public sealed record RestoreCostRequest
{
    public int  ChunksAvailable          { get; init; }
    public int  ChunksAlreadyRehydrated  { get; init; }
    public int  ChunksNeedingRehydration { get; init; }
    public int  ChunksPendingRehydration { get; init; }
    public long BytesNeedingRehydration  { get; init; }
    public long BytesPendingRehydration  { get; init; }
    public long DownloadBytes            { get; init; }

    public int  HotDownloadChunks  { get; init; }
    public long HotDownloadBytes   { get; init; }
    public int  CoolDownloadChunks { get; init; }
    public long CoolDownloadBytes  { get; init; }
    public int  ColdDownloadChunks { get; init; }
    public long ColdDownloadBytes  { get; init; }

    /// <summary>Months a rehydrated archive copy is assumed to be retained in the online tier.</summary>
    public double MonthsStored { get; init; } = 1.0;
}

/// <summary>
/// Restore cost estimate shown before downloads or rehydration begin. Provider-neutral: chunk counts/bytes
/// and the total (in EUR) at the two rehydration priorities (for providers without priority tiers,
/// <see cref="TotalStandard"/> == <see cref="TotalHigh"/>). The detailed per-component breakdown is a
/// provider implementation detail and is not part of this contract.
/// </summary>
public sealed record RestoreCostEstimate
{
    public required int  ChunksAvailable          { get; init; }
    public required int  ChunksAlreadyRehydrated  { get; init; }
    public required int  ChunksNeedingRehydration { get; init; }
    public required int  ChunksPendingRehydration { get; init; }
    public required long BytesNeedingRehydration  { get; init; }
    public required long BytesPendingRehydration  { get; init; }
    public required long DownloadBytes            { get; init; }

    /// <summary>Total estimated cost at Standard rehydration priority.</summary>
    public required double TotalStandard { get; init; }

    /// <summary>Total estimated cost at High rehydration priority (== Standard when no archive rehydration is involved).</summary>
    public required double TotalHigh { get; init; }

    /// <summary>Provider rehydration SLA at Standard priority — the upper bound on how long archive-tier
    /// chunks take to rehydrate before their files can be restored. Lets the UI render "up to {StandardWait}"
    /// and the Api compute a "≈ hydrated by" heuristic, without any host hardcoding a provider constant.</summary>
    public required TimeSpan StandardWait { get; init; }

    /// <summary>Provider rehydration SLA at High priority (faster, costlier). See <see cref="StandardWait"/>.</summary>
    public required TimeSpan HighWait { get; init; }
}
