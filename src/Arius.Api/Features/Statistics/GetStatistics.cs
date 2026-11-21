using System.ComponentModel;
using System.Text.Json;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;
using Arius.Core.Features.LatestSnapshotVersionQuery;
using Arius.Core.Features.StatisticsQuery;
using Arius.Core.Shared.Storage;
using Mediator;

namespace Arius.Api.Features.Statistics;

/// <summary>
/// Repository statistics, cached in the app database. Statistics are a pure function of the snapshot set, so a cache
/// hit is served without storage access; the cache is cleared when the snapshot set can change (after an archive, and
/// on a repository update or delete). <c>full=true</c> loads the whole chunk index so the repository-wide storage
/// figures are complete, which is slower.
/// </summary>
internal sealed class GetStatistics(AppDatabase database, RepositoryProviderRegistry registry) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/repos/{id:long}/stats", (
                long id,
                [Description("Snapshot version, or a prefix of it. Omit for the latest snapshot.")] string? version,
                [Description("Load the whole chunk index first, so the repository-wide figures are complete. Slower. Defaults to false.")] bool? full,
                CancellationToken ct,
                GetStatistics endpoint) => endpoint.HandleAsync(id, version, full, ct))
            .WithSummary("Get repository statistics")
            .WithDescription("Returns the file count and size of a snapshot, and the repository-wide deduplicated size, stored size, chunk count and estimated monthly storage cost per tier. Without `full=true`, the repository-wide figures cover only the part of the chunk index that is cached locally. Results are cached until the snapshots change. Returns zeros if the container does not exist yet.");

    private async Task<StatisticsDto> HandleAsync(long id, string? version, bool? full, CancellationToken ct)
    {
        var fullFlag   = full ?? false;
        var versionKey = version ?? string.Empty;

        var cached = database.GetCachedStatistics(id, versionKey, fullFlag);
        if (cached is not null)
            return JsonSerializer.Deserialize<StatisticsDto>(cached)!;

        // A container that does not exist yet (no archive has run) has no statistics: return zeros instead of a 500.
        ServiceProvider provider;
        try
        {
            provider = await registry.GetReadProviderAsync(id, ct);
        }
        catch (PreflightException ex) when (ex.ErrorKind == PreflightErrorKind.ContainerNotFound)
        {
            return new StatisticsDto(0, 0, 0, 0, 0, 0, []);
        }

        var mediator = provider.GetRequiredService<IMediator>();

        // The latest snapshot version fingerprints the cache entry so older generations can be pruned.
        var fingerprint = await mediator.Send(new LatestSnapshotVersionQuery(), ct) ?? string.Empty;

        var statistics = await mediator.Send(new StatisticsQuery(version, fullFlag), ct);
        var dto = new StatisticsDto(
            statistics.Files, statistics.OriginalSize, statistics.DeduplicatedSize, statistics.StoredSize, statistics.UniqueChunks,
            statistics.TotalStorageCostPerMonth,
            statistics.StoredByTier.Select(t => new TierStatisticsDto(t.Tier.ToString(), t.UniqueChunks, t.StoredSize, t.CostPerMonth)).ToList());

        database.UpsertCachedStatistics(id, versionKey, fullFlag, fingerprint, JsonSerializer.Serialize(dto));
        return dto;
    }
}
