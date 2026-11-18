namespace Arius.Api.Contracts;

public sealed record ScheduleDto(long Id, long RepoId, string Cron, string Kind, bool Enabled, DateTimeOffset? NextRun);
