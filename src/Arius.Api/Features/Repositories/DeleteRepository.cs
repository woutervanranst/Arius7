using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.Downloads;

namespace Arius.Api.Endpoints;

/// <summary><c>DELETE /repos/{id}</c></summary>
internal static class DeleteRepository
{
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapDelete("/{id:long}", (long id, AppDatabase db, RepositoryProviderRegistry registry, DownloadStaging staging) =>
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
        });
}
