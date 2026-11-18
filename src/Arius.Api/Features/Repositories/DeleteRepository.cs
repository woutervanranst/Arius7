using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Downloads;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Repositories;

/// <summary><c>DELETE /repos/{id}</c></summary>
internal sealed class DeleteRepository(AppDatabase db, RepositoryProviderRegistry registry, DownloadStaging staging) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapDelete("/repos/{id:long}", (long id, DeleteRepository endpoint) => endpoint.Handle(id));

    public IResult Handle(long id)
    {
        if (db.GetRepository(id) is null)
            return Results.NotFound();

        // Deleting disposes the repo's logger factory, which a concurrent job build resolves loggers from.
        if (db.HasActiveJob(id))
            return Results.Conflict("Repository has an active job; wait for it to finish or cancel it before deleting.");

        foreach (var job in db.ListDownloadJobs(id))
            staging.Delete(job.Id);

        db.DeleteRepository(id);
        registry.Remove(id); // repo is gone for good → also dispose its rolling-log factory, not just Evict the provider
        return Results.NoContent();
    }
}
