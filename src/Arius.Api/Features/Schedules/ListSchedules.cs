using Arius.Api.AppData;

namespace Arius.Api.Endpoints;

/// <summary><c>GET /repos/{id}/schedules</c></summary>
internal static class ListSchedules
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/repos/{id:long}/schedules", (long id, AppDatabase db) =>
            db.ListSchedules(id).Select(ScheduleEndpoints.ToDto).ToList());
}
