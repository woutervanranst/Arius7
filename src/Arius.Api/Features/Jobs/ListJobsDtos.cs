namespace Arius.Api.Features.Jobs;

/// <summary>A job in the job history.</summary>
/// <param name="Id">Job ID.</param>
/// <param name="RepoId">Repository ID.</param>
/// <param name="Repo">Repository alias.</param>
/// <param name="Kind"><c>archive</c>, <c>restore</c> or <c>download</c>.</param>
/// <param name="Trigger"><c>one-off</c> for a job a user started, or <c>schedule</c> for a scheduled archive.</param>
/// <param name="Status"><c>running</c>, <c>awaiting-cost</c> or <c>rehydrating</c> while active; <c>completed</c>, <c>failed</c>, <c>cancelled</c> or <c>interrupted</c> when finished.</param>
/// <param name="Pct">Progress, from 0 to 100.</param>
/// <param name="Detail">Result summary, or the reason the job failed or was cancelled.</param>
/// <param name="StartedAt">Start time.</param>
/// <param name="FinishedAt">End time, or <c>null</c> while the job is active.</param>
/// <param name="Outcome">Summary of a finished job as a JSON string (file counts, sizes, duration), or <c>null</c> while the job is active.</param>
public sealed record JobDto(
    string          Id,
    long            RepoId,
    string          Repo,
    string          Kind,
    string          Trigger,
    string          Status,
    double          Pct,
    string?         Detail,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string?         Outcome = null);
