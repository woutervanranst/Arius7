using System.ComponentModel;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Jobs;

internal sealed class ListJobs(AppDatabase db) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/jobs", (
                [Description("Only return jobs of this repository.")] long? repositoryId,
                [Description("`active` for running, awaiting-cost and rehydrating jobs; `terminal` for completed, failed, cancelled and interrupted jobs; or a single status. Omit for all jobs.")] string? status,
                ListJobs endpoint) => endpoint.Handle(repositoryId, status))
            .WithSummary("List jobs")
            .WithDescription("Returns jobs, newest first. Without `status=active`, only the 100 most recent jobs are considered.");

    private List<JobDto> Handle(long? repositoryId, string? status)
    {
        var aliases = db.ListRepositories().ToDictionary(r => r.Id, r => r.Alias);
        var nonTerminal = new HashSet<string>(JobStatuses.NonTerminal);
        // "active" uses an uncapped query: ListJobs() returns only the newest jobs, so filtering it could miss a
        // long-running job.
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
    }
}
