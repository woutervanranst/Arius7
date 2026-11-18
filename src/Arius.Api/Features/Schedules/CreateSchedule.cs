using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Schedules;

/// <summary><c>POST /repos/{id}/schedules</c></summary>
internal sealed class CreateSchedule(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/repos/{id:long}/schedules", (long id, CreateScheduleRequest request, CreateSchedule endpoint) => endpoint.Handle(id, request));

    public IResult Handle(long id, CreateScheduleRequest request)
    {
        if (db.GetRepository(id) is null) return Results.NotFound();
        var scheduleId = db.InsertSchedule(id, request.Cron, request.Kind ?? "archive", enabled: true);
        return Results.Created($"/api/repos/{id}/schedules/{scheduleId}", ScheduleEndpoints.ToDto(db.ListSchedules(id).First(s => s.Id == scheduleId)));
    }
}
