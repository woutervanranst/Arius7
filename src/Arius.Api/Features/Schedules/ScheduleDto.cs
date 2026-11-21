namespace Arius.Api.Features.Schedules;

/// <summary>A cron schedule that starts archive jobs for a repository.</summary>
/// <param name="Id">Schedule ID.</param>
/// <param name="RepoId">Repository ID.</param>
/// <param name="Cron">Cron expression, evaluated in UTC.</param>
/// <param name="Kind">Job kind.</param>
/// <param name="Enabled">Whether the schedule runs.</param>
/// <param name="NextRun">Next planned run, or <c>null</c> until the scheduler has computed it.</param>
public sealed record ScheduleDto(long Id, long RepoId, string Cron, string Kind, bool Enabled, DateTimeOffset? NextRun);
