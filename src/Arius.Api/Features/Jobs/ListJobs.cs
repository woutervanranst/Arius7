using Arius.Api.AppData;
using Arius.Api.Contracts;

namespace Arius.Api.Endpoints;

/// <summary><c>GET /jobs</c> — jobs history, optionally filtered by repository and status.</summary>
internal static class ListJobs
{
    public static void Map(IEndpointRouteBuilder app) =>
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
}
