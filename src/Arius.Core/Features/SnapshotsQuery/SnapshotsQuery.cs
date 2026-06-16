using Arius.Core.Shared.Snapshot;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.SnapshotsQuery;

// --- QUERY

/// <summary>
/// Mediator command: enumerate the snapshots of a repository (newest information needed by the
/// time-travel picker and scrubber). Returns a materialized list because snapshot sets are small
/// (one blob per snapshot) and the whole set is rendered at once.
/// </summary>
public sealed record SnapshotsQuery() : ICommand<IReadOnlyList<SnapshotInfo>>;

// --- RESULT

/// <summary>
/// One snapshot, as needed by the UI.
/// </summary>
/// <param name="Version">
/// The snapshot's version identifier — the snapshot blob filename (the timestamp formatted with
/// <see cref="SnapshotService.TimestampFormat"/>). This is exactly what
/// <c>ListQueryOptions.Version</c> / <c>RestoreOptions.Version</c> are <c>StartsWith</c>-matched
/// against, so the UI can round-trip it back into a list or restore. (The "v28"-style labels in the
/// design are purely UI ordinals derived from position.)
/// </param>
/// <param name="Timestamp">UTC creation time of the snapshot.</param>
/// <param name="FileCount">Total number of files in the snapshot.</param>
public sealed record SnapshotInfo(string Version, DateTimeOffset Timestamp, long FileCount);

// --- HANDLER

/// <summary>
/// Lists snapshots oldest → newest and resolves each manifest for its timestamp and file count.
/// </summary>
public sealed class SnapshotsQueryHandler(
    ISnapshotService              snapshots,
    ILogger<SnapshotsQueryHandler> logger)
    : ICommandHandler<SnapshotsQuery, IReadOnlyList<SnapshotInfo>>
{
    public async ValueTask<IReadOnlyList<SnapshotInfo>> Handle(SnapshotsQuery query, CancellationToken cancellationToken)
    {
        // ── Stage 1: list snapshot blobs (oldest → newest) ──────────────────────
        var blobNames = await snapshots.ListBlobNamesAsync(cancellationToken);

        // ── Stage 2: resolve each manifest (disk-cache-first) for timestamp + file count ──
        var result = new List<SnapshotInfo>(blobNames.Count);
        foreach (var blobName in blobNames)
        {
            var version  = snapshots.GetVersion(blobName);
            var manifest = await snapshots.ResolveAsync(version, cancellationToken);
            if (manifest is null)
            {
                logger.LogWarning("[snapshots] manifest for {Version} could not be resolved; skipping", version);
                continue;
            }

            result.Add(new SnapshotInfo(version, manifest.Timestamp, manifest.FileCount));
        }

        logger.LogDebug("[snapshots] returning {Count} snapshots", result.Count);
        return result;
    }
}
