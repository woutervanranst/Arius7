using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Jobs;

/// <summary>A job's history row plus its progress snapshot (live while running, otherwise persisted), warning count,
/// pending cost estimate and restore resume state.</summary>
public sealed record JobDetailDto(
    string Id, long RepoId, string Repo, string Kind, string Trigger, string Status,
    double Pct, string? Detail, System.DateTimeOffset? StartedAt, System.DateTimeOffset? FinishedAt,
    string? Outcome, Arius.Api.Shared.Jobs.JobSnapshot? Snapshot, int WarningCount, CostEstimateDto? Cost, ResumeInfo? Resume);
