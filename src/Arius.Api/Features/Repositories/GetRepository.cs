using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;

namespace Arius.Api.Features.Repositories;

internal sealed class GetRepository(AppDatabase db, RepositoryProviderRegistry registry) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/repos/{id:long}", (long id, CancellationToken ct, GetRepository endpoint) => endpoint.HandleAsync(id, ct));

    private async Task<IResult> HandleAsync(long id, CancellationToken ct)
    {
        var repository = db.GetRepository(id);
        if (repository is null)
            return Results.NotFound();
        var lastArchiveStarts = db.ListLatestArchiveStartedAt(id);
        return Results.Ok(await RepositoryEndpoints.ResolveRegionDtoAsync(
            db, repository, (rid, c) => RepositoryEndpoints.TryGetAccountInfoAsync(registry, rid, c), ct,
            lastArchiveStarts.TryGetValue(id, out var startedAt) ? startedAt : null));
    }
}
