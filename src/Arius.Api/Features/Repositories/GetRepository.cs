using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;

namespace Arius.Api.Features.Repositories;

/// <summary><c>GET /repos/{id}</c></summary>
internal static class GetRepository
{
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapGet("/{id:long}", async (long id, AppDatabase db, RepositoryProviderRegistry registry, CancellationToken ct) =>
        {
            var repository = db.GetRepository(id);
            if (repository is null)
                return Results.NotFound();
            var lastArchiveStarts = db.ListLatestArchiveStartedAt(id);
            return Results.Ok(await RepositoryEndpoints.ResolveRegionDtoAsync(
                db, repository, (rid, c) => RepositoryEndpoints.TryGetAccountInfoAsync(registry, rid, c), ct,
                lastArchiveStarts.TryGetValue(id, out var startedAt) ? startedAt : null));
        });
}
