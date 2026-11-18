using Arius.Api.AppData;
using Arius.Api.Composition;

namespace Arius.Api.Endpoints;

/// <summary><c>GET /repos</c></summary>
internal static class ListRepositories
{
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapGet("/", async (AppDatabase db, RepositoryProviderRegistry registry, CancellationToken ct) =>
        {
            var repositories = db.ListRepositories();
            var lastArchiveStarts = db.ListLatestArchiveStartedAt();
            var dtos = await Task.WhenAll(repositories.Select(r =>
                RepositoryEndpoints.ResolveRegionDtoAsync(
                    db, r, (id, c) => RepositoryEndpoints.TryGetAccountInfoAsync(registry, id, c), ct,
                    lastArchiveStarts.TryGetValue(r.Id, out var startedAt) ? startedAt : null)));
            return dtos.ToList();
        });
}
