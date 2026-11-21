using Arius.Api.Features.Repositories;
using Arius.Api.Shared.Composition;
using Arius.Api.Shared.Hosting;
using Arius.Core.Features.SnapshotsListQuery;
using Arius.Core.Shared.Storage;
using Mediator;

namespace Arius.Api.Features.Snapshots;

internal sealed class ListSnapshots(RepositoryProviderRegistry registry) : IEndpoint
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/repos/{id:long}/snapshots", (long id, CancellationToken ct, ListSnapshots endpoint) => endpoint.HandleAsync(id, ct))
            .WithSummary("List snapshots")
            .WithDescription("Returns the repository's snapshots, oldest first. Returns an empty list if the container does not exist yet because no archive has run.");

    private async Task<List<SnapshotDto>> HandleAsync(long id, CancellationToken ct)
    {
        var snapshots = new List<SnapshotDto>();

        // A container that does not exist yet (no archive has run) has no snapshots: return an empty list instead
        // of a 500.
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
    }
}
