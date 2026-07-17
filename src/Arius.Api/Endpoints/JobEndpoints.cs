using Arius.Api.AppData;
using Arius.Api.Contracts;
using Arius.Api.Jobs;

namespace Arius.Api.Endpoints;

/// <summary>Jobs history + per-repository cron schedules.</summary>
internal static class JobEndpoints
{
    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/jobs", (AppDatabase db, long? repositoryId, string? status) =>
        {
            var aliases = db.ListRepositories().ToDictionary(r => r.Id, r => r.Alias);
            var nonTerminal = new HashSet<string>(JobStatuses.NonTerminal);
            // "active" is served by an uncapped, repo-scoped query: filtering the globally capped ListJobs() in
            // memory could drop a long-lived non-terminal job that fell outside the newest-100 window.
            IEnumerable<JobRecord> jobs = status == "active"
                ? db.ListActiveJobs(repositoryId)
                : db.ListJobs()
                    .Where(j => repositoryId is null || j.RepositoryId == repositoryId)
                    .Where(j => status switch
                    {
                        null or ""  => true,
                        "terminal"  => !nonTerminal.Contains(j.Status),
                        var s       => j.Status == s,
                    });
            return jobs
                .Select(j => new JobDto(
                    j.Id, j.RepositoryId, aliases.GetValueOrDefault(j.RepositoryId, "—"),
                    j.Kind, j.Trigger, j.Status, j.Pct, j.Detail, j.StartedAt, j.FinishedAt, j.Outcome))
                .ToList();
        });

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

        app.MapGet("/jobs/{id}/warnings", (string id, AppDatabase db, JobStateRegistry jobStates) =>
        {
            var job = db.GetJob(id);
            if (job is null) return Results.NotFound();

            var view = JobViewResolver.Resolve(jobStates, id, job.StateJson);
            return Results.Ok(new JobWarningsDto(view.WarningCount, view.Warnings, Truncated: view.WarningCount > view.Warnings.Count));
        });

        app.MapGet("/repos/{id:long}/schedules", (long id, AppDatabase db) =>
            db.ListSchedules(id).Select(ToDto).ToList());

        app.MapPost("/repos/{id:long}/schedules", (long id, CreateScheduleRequest request, AppDatabase db) =>
        {
            if (db.GetRepository(id) is null) return Results.NotFound();
            var scheduleId = db.InsertSchedule(id, request.Cron, request.Kind ?? "archive", enabled: true);
            return Results.Created($"/api/repos/{id}/schedules/{scheduleId}", ToDto(db.ListSchedules(id).First(s => s.Id == scheduleId)));
        });

        app.MapDelete("/repos/{id:long}/schedules/{scheduleId:long}", (long id, long scheduleId, AppDatabase db) =>
        {
            db.DeleteSchedule(scheduleId);
            return Results.NoContent();
        });
    }

    private static ScheduleDto ToDto(ScheduleRecord s) => new(s.Id, s.RepositoryId, s.Cron, s.Kind, s.Enabled, s.NextRun);
}
