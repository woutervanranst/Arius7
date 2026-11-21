using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Arius.Api.Features.Schedules;

/// <summary><c>POST /repos/{id}/schedules</c></summary>
internal sealed class CreateSchedule(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/repos/{id:long}/schedules", (long id, CreateScheduleRequest request, CreateSchedule endpoint) => endpoint.Handle(id, request))
            .WithSummary("Create a schedule")
            .WithDescription("Adds an enabled cron schedule that starts archive jobs for the repository, using the repository's defaults. A run is skipped while the repository has an active job. Returns 201 Created with the schedule, or 404 Not Found if the repository does not exist.");

    private Results<Created<ScheduleDto>, NotFound> Handle(long id, CreateScheduleRequest request)
    {
        if (db.GetRepository(id) is null) return TypedResults.NotFound();
        var scheduleId = db.InsertSchedule(id, request.Cron, request.Kind ?? "archive", enabled: true);
        return TypedResults.Created($"/api/repos/{id}/schedules/{scheduleId}", ScheduleEndpoints.ToDto(db.ListSchedules(id).First(s => s.Id == scheduleId)));
    }
}
