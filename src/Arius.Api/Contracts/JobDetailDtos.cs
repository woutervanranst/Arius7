namespace Arius.Api.Contracts;

/// <summary>The cost-modal payload pushed on the <c>CostEstimate</c> message and returned by snapshot-on-attach
/// for an <c>awaiting-cost</c> job. jobId-tagged so a client attached to several jobs routes it (design §5, §8).
/// Wait windows are the provider SLAs surfaced via <c>RestoreCostEstimate</c> (Task 1) — the modal renders
/// "up to {standardWaitHours} h".</summary>
public sealed record CostEstimateDto(
    string JobId,
    int    ChunksAvailable,
    int    ChunksNeedingRehydration,
    long   BytesNeedingRehydration,
    long   DownloadBytes,
    double TotalStandard,
    double TotalHigh,
    double StandardWaitHours,
    double HighWaitHours);
