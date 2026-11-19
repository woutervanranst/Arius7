using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Schedules;

internal sealed class DeleteSchedule(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapDelete("/repos/{id:long}/schedules/{scheduleId:long}", (long id, long scheduleId, DeleteSchedule endpoint) => endpoint.Handle(id, scheduleId));

    private IResult Handle(long id, long scheduleId)
    {
        db.DeleteSchedule(scheduleId);
        return Results.NoContent();
    }
}
