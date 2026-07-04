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
            var nonTerminal = new HashSet<string> { "running", "awaiting-cost", "rehydrating" };
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
            if (jobStates.TryGet(id, out var sink))
            {
                snapshot = sink.BuildSnapshot(DateTimeOffset.UtcNow);
                warningCount = sink.WarningCount;
            }
            else if (job.StateJson is not null)
            {
                try
                {
                    var persisted = JsonSerializer.Deserialize<PersistedJobState>(job.StateJson);
                    snapshot = persisted?.Snapshot;
                    warningCount = persisted?.Warnings.Count ?? 0;
                }
                catch (JsonException) { /* leave snapshot null */ }
            }

            return Results.Ok(new JobDetailDto(
                job.Id, job.RepositoryId, repo?.Alias ?? "—", job.Kind, job.Trigger, job.Status,
                job.Pct, job.Detail, job.StartedAt, job.FinishedAt, job.Outcome, snapshot, warningCount));
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
                    return Results.Ok(new JobWarningsDto(lines.Count, lines, Truncated: false));
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
}
