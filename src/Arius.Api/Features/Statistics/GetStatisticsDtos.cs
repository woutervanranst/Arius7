namespace Arius.Api.Features.Statistics;

/// <summary>Repository statistics. <c>files</c> and <c>originalSize</c> apply to the selected
/// snapshot; the other figures are repository-wide, across all snapshots.</summary>
/// <param name="Files">Number of files in the snapshot.</param>
/// <param name="OriginalSize">Total original size of the files in the snapshot, in bytes.</param>
/// <param name="DeduplicatedSize">Original size of the distinct content, in bytes: the unique data before compression.</param>
/// <param name="StoredSize">Size of the distinct chunks in storage, in bytes: after deduplication and compression.</param>
/// <param name="UniqueChunks">Number of distinct chunks.</param>
/// <param name="TotalStorageCostPerMonth">Estimated monthly storage cost across all tiers, in EUR.</param>
/// <param name="StoredByTier">Chunk count, stored size and estimated monthly cost per storage tier.</param>
public sealed record StatisticsDto(
    long Files,
    long OriginalSize,
    long DeduplicatedSize,
    long StoredSize,
    long UniqueChunks,
    double TotalStorageCostPerMonth,
    IReadOnlyList<TierStatisticsDto> StoredByTier);

/// <summary>Stored size, distinct-chunk count, and estimated monthly storage cost for one storage tier (Hot/Cool/Cold/Archive).</summary>
/// <param name="Tier">Storage tier: <c>Hot</c>, <c>Cool</c>, <c>Cold</c> or <c>Archive</c>.</param>
/// <param name="UniqueChunks">Number of distinct chunks in this tier.</param>
/// <param name="StoredSize">Size of those chunks in storage, in bytes.</param>
/// <param name="CostPerMonth">Estimated monthly storage cost for this tier, in EUR.</param>
public sealed record TierStatisticsDto(string Tier, long UniqueChunks, long StoredSize, double CostPerMonth);
