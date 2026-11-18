using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Schedules;

/// <summary>Helpers shared by the <c>/repos/{id}/schedules</c> endpoints.</summary>
internal static class ScheduleEndpoints
{
    internal static ScheduleDto ToDto(ScheduleRecord s) => new(s.Id, s.RepositoryId, s.Cron, s.Kind, s.Enabled, s.NextRun);
}
