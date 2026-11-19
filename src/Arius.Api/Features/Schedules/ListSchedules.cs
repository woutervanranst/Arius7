using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Schedules;

/// <summary><c>GET /repos/{id}/schedules</c></summary>
internal sealed class ListSchedules(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/repos/{id:long}/schedules", (long id, ListSchedules endpoint) => endpoint.Handle(id));

    private List<ScheduleDto> Handle(long id) =>
        db.ListSchedules(id).Select(ScheduleEndpoints.ToDto).ToList();
}
