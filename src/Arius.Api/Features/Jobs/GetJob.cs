using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Jobs;

internal sealed class GetJob(AppDatabase db, JobStateRegistry jobStates) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/jobs/{id}", (string id, GetJob endpoint) => endpoint.Handle(id));

    private IResult Handle(string id)
    {
        var job = db.GetJob(id);
        if (job is null) return Results.NotFound();
        var repo = db.GetRepository(job.RepositoryId);

        var view = JobViewResolver.Resolve(jobStates, id, job.StateJson);
        return Results.Ok(new JobDetailDto(
            job.Id, job.RepositoryId, repo?.Alias ?? "—", job.Kind, job.Trigger, job.Status,
            job.Pct, job.Detail, job.StartedAt, job.FinishedAt, job.Outcome,
            view.Snapshot, view.WarningCount, view.Cost, view.Resume));
    }
}
