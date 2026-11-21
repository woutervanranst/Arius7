using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hosting;
using Arius.Api.Shared.Jobs;

namespace Arius.Api.Features.Jobs;

internal sealed class GetJobWarnings(AppDatabase db, JobStateRegistry jobStates) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/jobs/{id}/warnings", (string id, GetJobWarnings endpoint) => endpoint.Handle(id))
            .WithSummary("Get a job's warnings")
            .WithDescription("Returns the warnings a job emitted. Only the most recent warnings are kept. Returns 404 Not Found if the job does not exist.");

    private IResult Handle(string id)
    {
        var job = db.GetJob(id);
        if (job is null) return Results.NotFound();

        var view = JobViewResolver.Resolve(jobStates, id, job.StateJson);
        return Results.Ok(new JobWarningsDto(view.WarningCount, view.Warnings, Truncated: view.WarningCount > view.Warnings.Count));
    }
}
