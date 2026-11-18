using Arius.Core.Shared.Snapshot;
using Mediator;

namespace Arius.Core.Features.LatestSnapshotVersionQuery;

// --- QUERY

/// <summary>
/// Mediator query: the version of the repository's newest snapshot, or <c>null</c> when it has none.
/// Cheap by design — it lists the snapshot blob names only and resolves no manifest — so a host can use it
/// as a fingerprint of the snapshot set (e.g. to stamp cached statistics).
/// </summary>
public sealed record LatestSnapshotVersionQuery() : IQuery<string?>;

// --- HANDLER

public sealed class LatestSnapshotVersionQueryHandler(ISnapshotService snapshots)
    : IQueryHandler<LatestSnapshotVersionQuery, string?>
{
    public async ValueTask<string?> Handle(LatestSnapshotVersionQuery query, CancellationToken cancellationToken)
    {
        var blobNames = await snapshots.ListBlobNamesAsync(cancellationToken);
        return blobNames.Count == 0 ? null : snapshots.GetVersion(blobNames[^1]);
    }
}
