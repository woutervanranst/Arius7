namespace Arius.Api.Features.Statistics;

public sealed record StatisticsDto(
    long Files,
    long OriginalSize,
    long DeduplicatedSize,
    long StoredSize,
    long UniqueChunks,
    double TotalStorageCostPerMonth,
    IReadOnlyList<TierStatisticsDto> StoredByTier);

/// <summary>Stored size, distinct-chunk count, and estimated monthly storage cost for one storage tier (Hot/Cool/Cold/Archive).</summary>
public sealed record TierStatisticsDto(string Tier, long UniqueChunks, long StoredSize, double CostPerMonth);
