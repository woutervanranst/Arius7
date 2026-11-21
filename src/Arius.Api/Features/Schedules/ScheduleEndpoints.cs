using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Schedules;

internal static class ScheduleEndpoints
{
    internal static ScheduleDto ToDto(ScheduleRecord s) => new(s.Id, s.RepositoryId, s.Cron, s.Kind, s.Enabled, s.NextRun);
}
