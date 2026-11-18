using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Jobs;

/// <summary><c>GET /jobs/{id}/warnings</c> — the job's verbatim warnings tail.</summary>
internal static class GetJobWarnings
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/jobs/{id}/warnings", (string id, AppDatabase db, JobStateRegistry jobStates) =>
        {
            var job = db.GetJob(id);
            if (job is null) return Results.NotFound();

            var view = JobViewResolver.Resolve(jobStates, id, job.StateJson);
            return Results.Ok(new JobWarningsDto(view.WarningCount, view.Warnings, Truncated: view.WarningCount > view.Warnings.Count));
        });
}
