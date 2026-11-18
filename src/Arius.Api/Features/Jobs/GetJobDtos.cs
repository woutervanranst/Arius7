namespace Arius.Api.Contracts;

/// <summary>Full single-job payload for GET /jobs/{id}: the history row plus the parsed progress snapshot
/// (live if running, else from state_json) and the warning count.</summary>
public sealed record JobDetailDto(
    string Id, long RepoId, string Repo, string Kind, string Trigger, string Status,
    double Pct, string? Detail, System.DateTimeOffset? StartedAt, System.DateTimeOffset? FinishedAt,
    string? Outcome, Arius.Api.Jobs.JobSnapshot? Snapshot, int WarningCount, CostEstimateDto? Cost, ResumeInfo? Resume);
