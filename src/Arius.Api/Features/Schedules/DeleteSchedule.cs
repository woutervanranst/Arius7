using Arius.Api.Shared.AppData;

namespace Arius.Api.Features.Schedules;

/// <summary><c>DELETE /repos/{id}/schedules/{scheduleId}</c></summary>
internal static class DeleteSchedule
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapDelete("/repos/{id:long}/schedules/{scheduleId:long}", (long id, long scheduleId, AppDatabase db) =>
        {
            db.DeleteSchedule(scheduleId);
            return Results.NoContent();
        });
}
