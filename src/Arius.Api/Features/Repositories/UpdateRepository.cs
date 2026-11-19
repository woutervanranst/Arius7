using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Repositories;

internal sealed class UpdateRepository(AppDatabase db, SecretProtector secrets, RepositoryProviderRegistry registry) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPatch("/repos/{id:long}", (long id, UpdateRepositoryRequest request, UpdateRepository endpoint) => endpoint.Handle(id, request));

    private IResult Handle(long id, UpdateRepositoryRequest request)
    {
        if (db.GetRepository(id) is null)
            return Results.NotFound();

        db.UpdateRepository(
            id,
            request.Alias,
            request.LocalPath,
            secrets.Protect(request.Passphrase));

        // The connection may have changed: rebuild the read provider and discard the cached statistics and region.
        registry.Evict(id);
        db.ClearStatisticsCache(id);
        db.SetRepositoryRegionHint(id, null);
        // The region is resolved and cached again when the client refetches the list.
        var lastArchiveStarts = db.ListLatestArchiveStartedAt(id);
        return Results.Ok(RepositoryEndpoints.ToDto(
            db, db.GetRepository(id)!, region: null, regionIsDefault: false,
            lastArchiveStarts.TryGetValue(id, out var startedAt) ? startedAt : null));
    }
}
