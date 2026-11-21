using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Arius.Api.Features.Schedules;

internal sealed class DeleteSchedule(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapDelete("/repos/{id:long}/schedules/{scheduleId:long}", (long id, long scheduleId, DeleteSchedule endpoint) => endpoint.Handle(id, scheduleId))
            .WithSummary("Delete a schedule")
            .WithDescription("Returns 204 No Content, also when the schedule does not exist.");

    private NoContent Handle(long id, long scheduleId)
    {
        db.DeleteSchedule(scheduleId);
        return TypedResults.NoContent();
    }
}
