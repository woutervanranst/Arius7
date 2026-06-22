using System.Text.Json;
using Arius.Api.AppData;
using Arius.Api.Composition;
using Arius.Api.Contracts;
using Arius.Core.Features.SnapshotsQuery;
using Arius.Core.Features.StatisticsQuery;
using Arius.Core.Shared.Snapshot;
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

        // `full=true` loads the whole chunk index so the repository-wide storage figures are complete
        // (slower); the web Statistics screen lazy-loads its storage section with that flag.
        //
        // The result is memoized in the app database (see statistics_cache). A repository's statistics are
        // a pure function of its snapshot set, so a cache HIT is served straight from the local database
        // with NO blob-storage access — that is what makes a warm load fast. The cache is invalidated
        // explicitly when the snapshot set can have changed: after an archive (JobRunner) and on a
        // properties change / delete (RepositoryEndpoints). Only on a MISS do we touch storage: list the
        // snapshot blobs once to stamp the entry's fingerprint (the latest snapshot version, for
        // provenance + pruning prior generations) and run the real computation.
        app.MapGet("/repos/{id:long}/stats", async (long id, string? version, bool? full, AppDatabase database, RepositoryProviderRegistry registry, CancellationToken ct) =>
        {
            var fullFlag   = full ?? false;
            var versionKey = version ?? string.Empty;

            var cached = database.GetCachedStatistics(id, versionKey, fullFlag);
            if (cached is not null)
                return JsonSerializer.Deserialize<StatisticsDto>(cached)!;

            var provider = await registry.GetReadProviderAsync(id, ct);
            var mediator = provider.GetRequiredService<IMediator>();

            // Miss: derive the fingerprint cheaply (latest blob name only — not SnapshotsQuery, which
            // resolves every manifest), compute, and store.
            var snapshotService = provider.GetRequiredService<ISnapshotService>();
            var snapshotBlobs   = await snapshotService.ListBlobNamesAsync(ct);
            var fingerprint     = snapshotBlobs.Count == 0 ? string.Empty : snapshotService.GetVersion(snapshotBlobs[^1]);

            var statistics = await mediator.Send(new StatisticsQuery(version, fullFlag), ct);
            var dto = new StatisticsDto(
                statistics.Files, statistics.OriginalSize, statistics.DeduplicatedSize, statistics.StoredSize, statistics.UniqueChunks,
                statistics.StoredByTier.Select(t => new TierStatisticsDto(t.Tier.ToString(), t.UniqueChunks, t.StoredSize)).ToList());

            database.UpsertCachedStatistics(id, versionKey, fullFlag, fingerprint, JsonSerializer.Serialize(dto));
            return dto;
        });
    }
}
