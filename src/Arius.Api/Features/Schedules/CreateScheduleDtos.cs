namespace Arius.Api.Features.Schedules;

public sealed record CreateScheduleRequest(string Cron, string? Kind);
