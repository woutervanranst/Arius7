namespace Arius.Api.Contracts;

public sealed record CreateScheduleRequest(string Cron, string? Kind);
