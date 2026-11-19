namespace Arius.Api.Shared.Jobs;

/// <summary>The restore cost prompt, sent as the <c>CostEstimate</c> message and returned on attach to an
/// <c>awaiting-cost</c> job. Carries the job id so a client attached to several jobs can route it. The wait hours
/// are the storage provider's rehydration SLAs.</summary>
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
