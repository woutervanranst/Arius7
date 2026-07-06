using System.Text.Json;
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
            return db.ListJobs()
                .Where(j => repositoryId is null || j.RepositoryId == repositoryId)
                .Where(j => status switch
                {
                    null or ""  => true,
                    "active"    => nonTerminal.Contains(j.Status),
                    "terminal"  => !nonTerminal.Contains(j.Status),
                    var s       => j.Status == s,
                })
                .Select(j => new JobDto(
                    j.Id, j.RepositoryId, aliases.GetValueOrDefault(j.RepositoryId, "—"),
                    j.Kind, j.Trigger, j.Status, j.Pct, j.Detail, j.StartedAt, j.FinishedAt, j.Outcome))
                .ToList();
        });

        app.MapGet("/jobs/{id}", (string id, AppDatabase db, JobStateRegistry jobStates) =>
        {
            var job = db.GetJob(id);
            if (job is null) return Results.NotFound();
            var repo = db.ListRepositories().FirstOrDefault(r => r.Id == job.RepositoryId);

            JobSnapshot? snapshot = null;
            var warningCount = 0;
            CostEstimateDto? cost = null;
            ResumeInfo? resume = null;
            // A job blocked in the ConfirmRehydration callback (genuinely parked at awaiting-cost, still within
            // the approval window) still has a LIVE sink here — JobRunner's method has not returned, so nothing
            // has removed it from jobStates yet. Read the cost/resume the run staged on the sink for exactly
            // this case (ReattachScenarioTests proves it) rather than hardcoding null.
            if (jobStates.TryGet(id, out var sink))
            {
                snapshot = sink.BuildSnapshot(DateTimeOffset.UtcNow);
                warningCount = sink.WarningCount;
                cost = sink.PendingCost;
                resume = ToResumeInfo(sink.PendingResume);
            }
            else if (job.StateJson is not null)
            {
                try
                {
                    var persisted = JsonSerializer.Deserialize<PersistedJobState>(job.StateJson);
                    snapshot = persisted?.Snapshot;
                    warningCount = persisted?.Snapshot.WarningCount ?? 0;
                    cost = persisted?.Cost;
                    resume = ToResumeInfo(persisted?.Resume);
                }
                catch (JsonException) { /* leave snapshot null */ }
            }

            return Results.Ok(new JobDetailDto(
                job.Id, job.RepositoryId, repo?.Alias ?? "—", job.Kind, job.Trigger, job.Status,
                job.Pct, job.Detail, job.StartedAt, job.FinishedAt, job.Outcome, snapshot, warningCount, cost, resume));
        });

        app.MapGet("/jobs/{id}/warnings", (string id, AppDatabase db, JobStateRegistry jobStates) =>
        {
            var job = db.GetJob(id);
            if (job is null) return Results.NotFound();

            if (jobStates.TryGet(id, out var sink))
            {
                var lines = sink.Warnings;
                return Results.Ok(new JobWarningsDto(sink.WarningCount, lines, Truncated: sink.WarningCount > lines.Count));
            }
            if (job.StateJson is not null)
            {
                try
                {
                    var persisted = JsonSerializer.Deserialize<PersistedJobState>(job.StateJson);
                    var lines = persisted?.Warnings ?? [];
                    var total = persisted?.Snapshot.WarningCount ?? lines.Count;
                    return Results.Ok(new JobWarningsDto(total, lines, Truncated: total > lines.Count));
                }
                catch (JsonException) { /* fall through */ }
            }
            return Results.Ok(new JobWarningsDto(0, [], false));
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

    private static ResumeInfo? ToResumeInfo(RestoreResumeState? r) =>
        r is null ? null : new ResumeInfo(r.AutoResume, r.RehydrationStartedAt, r.RehydrationWindow.TotalHours);
}
