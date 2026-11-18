using System.Text.Json;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;
using Arius.Core.Features.LatestSnapshotVersionQuery;
using Arius.Core.Features.StatisticsQuery;
using Arius.Core.Shared.Storage;
using Mediator;

namespace Arius.Api.Features.Statistics;

/// <summary><c>GET /repos/{id}/stats</c> — repository statistics.</summary>
internal sealed class GetStatistics(AppDatabase database, RepositoryProviderRegistry registry) : IEndpoint
{
    // `full=true` loads the whole chunk index so the repository-wide storage figures are complete
    // (slower); the web Statistics screen lazy-loads its storage section with that flag.
    //
    // The result is memoized in the app database (see statistics_cache). A repository's statistics are
    // a pure function of its snapshot set, so a cache HIT is served straight from the local database
    // with NO blob-storage access — that is what makes a warm load fast. The cache is invalidated
    // explicitly when the snapshot set can have changed: after an archive (JobRunner) and on a
    // properties change / delete (UpdateRepository / DeleteRepository). Only on a MISS do we touch storage: ask Core for
    // the latest snapshot version once to stamp the entry's fingerprint (for provenance + pruning prior
    // generations) and run the real computation.
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/repos/{id:long}/stats", (long id, string? version, bool? full, CancellationToken ct, GetStatistics endpoint) => endpoint.HandleAsync(id, version, full, ct));

    public async Task<StatisticsDto> HandleAsync(long id, string? version, bool? full, CancellationToken ct)
    {
        var fullFlag   = full ?? false;
        var versionKey = version ?? string.Empty;

        var cached = database.GetCachedStatistics(id, versionKey, fullFlag);
        if (cached is not null)
            return JsonSerializer.Deserialize<StatisticsDto>(cached)!;

        // A repository whose container hasn't been created yet (no archive has run) has no
        // statistics by definition — degrade to zeroed-out stats rather than surfacing a 500.
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

        // Miss: derive the fingerprint cheaply (latest blob name only — not SnapshotsListQuery, which
        // resolves every manifest), compute, and store.
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
