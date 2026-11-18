using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Repositories;

/// <summary><c>PATCH /repos/{id}</c> — properties-screen update.</summary>
internal sealed class UpdateRepository(AppDatabase db, SecretProtector secrets, RepositoryProviderRegistry registry) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPatch("/repos/{id:long}", (long id, UpdateRepositoryRequest request, UpdateRepository endpoint) => endpoint.Handle(id, request));

    public IResult Handle(long id, UpdateRepositoryRequest request)
    {
        if (db.GetRepository(id) is null)
            return Results.NotFound();

        db.UpdateRepository(
            id,
            request.Alias,
            request.LocalPath,
            secrets.Protect(request.Passphrase));

        // Connection material may have changed — drop the cached read provider so it rebuilds, discard any
        // memoized statistics, and invalidate the region cache so it re-resolves against the (possibly new) target.
        registry.Evict(id);
        db.ClearStatisticsCache(id);
        db.SetRepositoryRegionHint(id, null);
        // Region is left unresolved here; the client refetches the list, which re-resolves and caches it.
        var lastArchiveStarts = db.ListLatestArchiveStartedAt(id);
        return Results.Ok(RepositoryEndpoints.ToDto(
            db, db.GetRepository(id)!, region: null, regionIsDefault: false,
            lastArchiveStarts.TryGetValue(id, out var startedAt) ? startedAt : null));
    }
}
