using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Schedules;

/// <summary><c>POST /repos/{id}/schedules</c></summary>
internal static class CreateSchedule
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/repos/{id:long}/schedules", (long id, CreateScheduleRequest request, AppDatabase db) =>
        {
            if (db.GetRepository(id) is null) return Results.NotFound();
            var scheduleId = db.InsertSchedule(id, request.Cron, request.Kind ?? "archive", enabled: true);
            return Results.Created($"/api/repos/{id}/schedules/{scheduleId}", ScheduleEndpoints.ToDto(db.ListSchedules(id).First(s => s.Id == scheduleId)));
        });
}
