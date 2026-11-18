using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Schedules;

/// <summary>Per-repository cron schedules.</summary>
internal static class ScheduleEndpoints
{
    public static void MapScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        ListSchedules.Map(app);
        CreateSchedule.Map(app);
        DeleteSchedule.Map(app);
    }

    internal static ScheduleDto ToDto(ScheduleRecord s) => new(s.Id, s.RepositoryId, s.Cron, s.Kind, s.Enabled, s.NextRun);
}
