using Arius.Api.Composition;
using Arius.Api.Contracts;
using Arius.Core.Features.SnapshotsQuery;
using Arius.Core.Features.StatisticsQuery;
using Mediator;

namespace Arius.Api.Endpoints;

/// <summary>Read-only repository browsing endpoints: snapshots (time-travel) and statistics.</summary>
internal static class BrowseEndpoints
{
    public static void MapBrowseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/repos/{id:long}/snapshots", async (long id, RepositoryProviderRegistry registry, CancellationToken ct) =>
        {
            var provider = await registry.GetReadProviderAsync(id, ct);
            var mediator = provider.GetRequiredService<IMediator>();
            var snapshots = await mediator.Send(new SnapshotsQuery(), ct);
            return snapshots.Select(s => new SnapshotDto(s.Version, s.Timestamp, s.FileCount)).ToList();
        });

        app.MapGet("/repos/{id:long}/stats", async (long id, string? version, RepositoryProviderRegistry registry, CancellationToken ct) =>
        {
            var provider = await registry.GetReadProviderAsync(id, ct);
            var mediator = provider.GetRequiredService<IMediator>();
            var stats = await mediator.Send(new StatisticsQuery(version), ct);
            return new StatisticsDto(
                stats.Files, stats.OriginalSize, stats.DeduplicatedSize, stats.StoredSize, stats.UniqueChunks,
                stats.StoredByTier.Select(t => new TierStatisticsDto(t.Tier.ToString(), t.UniqueChunks, t.StoredSize)).ToList());
        });
    }
}
