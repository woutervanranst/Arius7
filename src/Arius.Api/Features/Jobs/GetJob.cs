using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Jobs;

/// <summary><c>GET /jobs/{id}</c> — one job with its progress snapshot.</summary>
internal static class GetJob
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/jobs/{id}", (string id, AppDatabase db, JobStateRegistry jobStates) =>
        {
            var job = db.GetJob(id);
            if (job is null) return Results.NotFound();
            var repo = db.GetRepository(job.RepositoryId);

            // A job blocked in the ConfirmRehydration callback (genuinely parked at awaiting-cost, still within
            // the approval window) still has a LIVE sink here — JobRunner's method has not returned, so nothing
            // has removed it from jobStates yet. JobViewResolver reads the cost/resume the run staged on the sink
            // for exactly this case rather than hardcoding null.
            var view = JobViewResolver.Resolve(jobStates, id, job.StateJson);
            return Results.Ok(new JobDetailDto(
                job.Id, job.RepositoryId, repo?.Alias ?? "—", job.Kind, job.Trigger, job.Status,
                job.Pct, job.Detail, job.StartedAt, job.FinishedAt, job.Outcome,
                view.Snapshot, view.WarningCount, view.Cost, view.Resume));
        });
}
