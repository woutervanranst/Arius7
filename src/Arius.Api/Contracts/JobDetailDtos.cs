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

/// <summary>Snapshot-on-attach payload (design §5): the job's current status, its absolute progress snapshot,
/// the cost modal if it is awaiting-cost, and the live warning count. One round trip, one client apply-path.</summary>
public sealed record JobAttachState(string Status, Arius.Api.Jobs.JobSnapshot Snapshot, CostEstimateDto? Cost, int WarningCount);

/// <summary>Full single-job payload for GET /jobs/{id}: the history row plus the parsed progress snapshot
/// (live if running, else from state_json) and the warning count.</summary>
public sealed record JobDetailDto(
    string Id, long RepoId, string Repo, string Kind, string Trigger, string Status,
    double Pct, string? Detail, System.DateTimeOffset? StartedAt, System.DateTimeOffset? FinishedAt,
    string? Outcome, Arius.Api.Jobs.JobSnapshot? Snapshot, int WarningCount);

/// <summary>Verbatim per-job warnings for GET /jobs/{id}/warnings. <see cref="Truncated"/> is true when more than
/// the retained tail (200) were emitted, so <see cref="Count"/> &gt; <see cref="Lines"/>.Count.</summary>
public sealed record JobWarningsDto(int Count, IReadOnlyList<string> Lines, bool Truncated);
