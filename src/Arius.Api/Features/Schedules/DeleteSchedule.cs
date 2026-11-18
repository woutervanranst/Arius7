using Arius.Api.AppData;

namespace Arius.Api.Endpoints;

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
