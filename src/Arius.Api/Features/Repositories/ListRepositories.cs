using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Repositories;

/// <summary><c>GET /repos</c></summary>
internal sealed class ListRepositories(AppDatabase db, RepositoryProviderRegistry registry) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/repos/", (CancellationToken ct, ListRepositories endpoint) => endpoint.HandleAsync(ct));

    public async Task<List<RepositoryDto>> HandleAsync(CancellationToken ct)
    {
        var repositories = db.ListRepositories();
        var lastArchiveStarts = db.ListLatestArchiveStartedAt();
        var dtos = await Task.WhenAll(repositories.Select(r =>
            RepositoryEndpoints.ResolveRegionDtoAsync(
                db, r, (id, c) => RepositoryEndpoints.TryGetAccountInfoAsync(registry, id, c), ct,
                lastArchiveStarts.TryGetValue(r.Id, out var startedAt) ? startedAt : null)));
        return dtos.ToList();
    }
}
