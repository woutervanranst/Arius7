namespace Arius.Api.Shared.Jobs;

/// <summary>The restore cost prompt, sent as the <c>CostEstimate</c> message and returned on attach to an
/// <c>awaiting-cost</c> job. Carries the job id so a client attached to several jobs can route it. The wait hours
/// are the storage provider's rehydration SLAs.</summary>
/// <param name="JobId">Restore or download job ID.</param>
/// <param name="ChunksAvailable">Chunks in an online tier, or already rehydrated, that can be downloaded now.</param>
/// <param name="ChunksNeedingRehydration">Archive-tier chunks that must be rehydrated first.</param>
/// <param name="BytesNeedingRehydration">Size of those chunks, in bytes.</param>
/// <param name="DownloadBytes">Total size to download, in bytes.</param>
/// <param name="TotalStandard">Estimated total cost at Standard rehydration priority, in EUR.</param>
/// <param name="TotalHigh">Estimated total cost at High rehydration priority, in EUR. Equal to <c>totalStandard</c> when nothing needs rehydration.</param>
/// <param name="StandardWaitHours">Maximum rehydration time at Standard priority, in hours.</param>
/// <param name="HighWaitHours">Maximum rehydration time at High priority, in hours.</param>
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
