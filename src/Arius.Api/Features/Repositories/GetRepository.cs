using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Arius.Api.Features.Repositories;

internal sealed class GetRepository(AppDatabase db, RepositoryProviderRegistry registry) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/repos/{id:long}", (long id, CancellationToken ct, GetRepository endpoint) => endpoint.HandleAsync(id, ct))
            .WithSummary("Get a repository")
            .WithDescription("Returns 404 Not Found if the repository does not exist.");

    private async Task<Results<Ok<RepositoryDto>, NotFound>> HandleAsync(long id, CancellationToken ct)
    {
        var repository = db.GetRepository(id);
        if (repository is null)
            return TypedResults.NotFound();
        var lastArchiveStarts = db.ListLatestArchiveStartedAt(id);
        return TypedResults.Ok(await RepositoryEndpoints.ResolveRegionDtoAsync(
            db, repository, (rid, c) => RepositoryEndpoints.TryGetAccountInfoAsync(registry, rid, c), ct,
            lastArchiveStarts.TryGetValue(id, out var startedAt) ? startedAt : null));
    }
}
