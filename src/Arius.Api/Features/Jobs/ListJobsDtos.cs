namespace Arius.Api.Features.Jobs;

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
