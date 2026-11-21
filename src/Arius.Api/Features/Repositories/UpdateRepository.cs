using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Arius.Api.Features.Repositories;

internal sealed class UpdateRepository(AppDatabase db, SecretProtector secrets, RepositoryProviderRegistry registry) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPatch("/repos/{id:long}", (long id, UpdateRepositoryRequest request, UpdateRepository endpoint) => endpoint.Handle(id, request))
            .WithSummary("Update a repository")
            .WithDescription("Changes the alias, local path or passphrase; null fields are left unchanged. The cached connection, statistics and pricing region are discarded, so the region in the response is null until the next list request. Returns 404 Not Found if the repository does not exist.");

    private Results<Ok<RepositoryDto>, NotFound> Handle(long id, UpdateRepositoryRequest request)
    {
        if (db.GetRepository(id) is null)
            return TypedResults.NotFound();

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
        return TypedResults.Ok(RepositoryEndpoints.ToDto(
            db, db.GetRepository(id)!, region: null, regionIsDefault: false,
            lastArchiveStarts.TryGetValue(id, out var startedAt) ? startedAt : null));
    }
}
