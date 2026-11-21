using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Repositories;

internal sealed class ListRepositories(AppDatabase db, RepositoryProviderRegistry registry) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/repos/", (CancellationToken ct, ListRepositories endpoint) => endpoint.HandleAsync(ct))
            .WithSummary("List repositories")
            .WithDescription("Returns all repositories. Passphrases and account keys are never returned. The pricing region is read from storage on first request and then cached; it is null when the container cannot be reached.");

    private async Task<List<RepositoryDto>> HandleAsync(CancellationToken ct)
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
