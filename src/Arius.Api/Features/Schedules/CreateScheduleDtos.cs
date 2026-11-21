namespace Arius.Api.Features.Schedules;

/// <summary>A schedule to create.</summary>
/// <param name="Cron">Cron expression in the standard five-field format, evaluated in UTC.</param>
/// <param name="Kind">Job kind. Defaults to <c>archive</c>, the only kind the scheduler runs.</param>
public sealed record CreateScheduleRequest(string Cron, string? Kind);
