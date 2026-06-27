using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.Cost;

// ── Storage cost (Statistics tab) ─────────────────────────────────────────────

/// <summary>Per-tier monthly storage cost breakdown for one region.</summary>
public sealed record StorageCostEstimate(
    string Region,
    string Currency,
    IReadOnlyList<TierStorageCost> Tiers,
    double TotalPerMonth);

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

    public int  HotDownloadChunks  { get; init; }  public long HotDownloadBytes  { get; init; }
    public int  CoolDownloadChunks { get; init; }  public long CoolDownloadBytes { get; init; }
    public int  ColdDownloadChunks { get; init; }  public long ColdDownloadBytes { get; init; }

    /// <summary>Months a rehydrated archive copy is assumed to be retained in the online tier.</summary>
    public double MonthsStored { get; init; } = 1.0;
}

/// <summary>
/// Restore cost estimate shown before downloads or rehydration begin. Provider-neutral: chunk counts/bytes,
/// the currency, and the total at the two rehydration priorities (for providers without priority tiers,
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

    /// <summary>Currency the totals are expressed in (from the provider's pricing, e.g. EUR).</summary>
    public required string Currency { get; init; }

    /// <summary>Total estimated cost at Standard rehydration priority.</summary>
    public required double TotalStandard { get; init; }

    /// <summary>Total estimated cost at High rehydration priority (== Standard when no archive rehydration is involved).</summary>
    public required double TotalHigh { get; init; }
}
