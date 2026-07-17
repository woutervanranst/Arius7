namespace Arius.Api.Contracts;

/// <summary>The cost-modal payload pushed on the <c>CostEstimate</c> message and returned by snapshot-on-attach
/// for an <c>awaiting-cost</c> job. jobId-tagged so a client attached to several jobs routes it.
/// Wait windows are the provider SLAs surfaced via <c>RestoreCostEstimate</c> — the modal renders
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

/// <summary>The parked-restore resume facts a reattaching client needs: whether auto-resume is on, and the
/// rehydration SLA window ("≈ hydrated by" = RehydrationStartedAt + RehydrationWindowHours). Null for jobs
/// with no restore-resume state.</summary>
public sealed record ResumeInfo(bool AutoResume, System.DateTimeOffset RehydrationStartedAt, double RehydrationWindowHours)
{
    /// <summary>Maps the persisted restore-resume state to the wire DTO (null-safe). Shared by JobsHub + JobEndpoints.</summary>
    public static ResumeInfo? From(Arius.Api.Jobs.RestoreResumeState? r) =>
        r is null ? null : new ResumeInfo(r.AutoResume, r.RehydrationStartedAt, r.RehydrationWindow.TotalHours);
}

/// <summary>Snapshot-on-attach payload: the job's current status, its absolute progress snapshot,
/// the cost modal if it is awaiting-cost, and the live warning count. One round trip, one client apply-path.</summary>
public sealed record JobAttachState(string Status, Arius.Api.Jobs.JobSnapshot Snapshot, CostEstimateDto? Cost, int WarningCount, ResumeInfo? Resume);

/// <summary>Full single-job payload for GET /jobs/{id}: the history row plus the parsed progress snapshot
/// (live if running, else from state_json) and the warning count.</summary>
public sealed record JobDetailDto(
    string Id, long RepoId, string Repo, string Kind, string Trigger, string Status,
    double Pct, string? Detail, System.DateTimeOffset? StartedAt, System.DateTimeOffset? FinishedAt,
    string? Outcome, Arius.Api.Jobs.JobSnapshot? Snapshot, int WarningCount, CostEstimateDto? Cost, ResumeInfo? Resume);

/// <summary>Verbatim per-job warnings for GET /jobs/{id}/warnings. <see cref="Truncated"/> is true when more than
/// the retained tail (200) were emitted, so <see cref="Count"/> &gt; <see cref="Lines"/>.Count.</summary>
public sealed record JobWarningsDto(int Count, IReadOnlyList<string> Lines, bool Truncated);
