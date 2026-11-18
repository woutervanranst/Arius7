using Arius.Api.Features.Repositories;
using Arius.Api.Shared.Composition;
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Shared.Storage;
using Mediator;

namespace Arius.Api.Features.Snapshots;

/// <summary><c>GET /repos/{id}/snapshots</c> — the repository's snapshots (time-travel).</summary>
internal static class ListSnapshots
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/repos/{id:long}/snapshots", async (long id, RepositoryProviderRegistry registry, CancellationToken ct) =>
        {
            var snapshots = new List<SnapshotDto>();

            // A repository whose container hasn't been created yet (no archive has run) has no
            // snapshots by definition — degrade to empty rather than surfacing a 500, mirroring
            // RepositoryEndpoints.TryGetAccountInfoAsync.
            ServiceProvider provider;
            try
            {
                provider = await registry.GetReadProviderAsync(id, ct);
            }
            catch (PreflightException ex) when (ex.ErrorKind == PreflightErrorKind.ContainerNotFound)
            {
                return snapshots;
            }

            var mediator = provider.GetRequiredService<IMediator>();
            await foreach (var s in mediator.CreateStream(new SnapshotsListQuery(), ct))
                snapshots.Add(new SnapshotDto(s.Version, s.Timestamp, s.FileCount));
            return snapshots;
        });
}
