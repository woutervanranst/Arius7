using System.Runtime.CompilerServices;
using Arius.Core.Shared.Snapshot;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.SnapshotsListQuery;

// --- QUERY

/// <summary>
/// Mediator stream query: enumerate the snapshots of a repository (oldest → newest), as needed by
/// the time-travel picker/scrubber and the CLI <c>snapshot list</c>. Streams so callers render rows
/// as each manifest resolves.
/// </summary>
public sealed record SnapshotsListQuery() : IStreamQuery<SnapshotInfo>;

// --- RESULT

/// <summary>
/// One snapshot, as needed by the UI / CLI.
/// </summary>
/// <param name="Version">
/// The snapshot's version identifier — the snapshot blob filename (the timestamp formatted with
/// <see cref="SnapshotService.TimestampFormat"/>). Exactly what <c>ListQueryOptions.Version</c> /
/// <c>RestoreOptions.Version</c> / <c>SnapshotDiffQuery</c> are <c>StartsWith</c>-matched against.
/// </param>
/// <param name="Timestamp">UTC creation time of the snapshot.</param>
/// <param name="FileCount">Total number of files in the snapshot.</param>
public sealed record SnapshotInfo(string Version, DateTimeOffset Timestamp, long FileCount);

// --- HANDLER

/// <summary>
/// Streams snapshots oldest → newest, resolving each manifest (disk-cache-first) for its timestamp
/// and file count. Unresolvable manifests are logged and skipped.
/// </summary>
public sealed class SnapshotsListQueryHandler(
    ISnapshotService                   snapshots,
    ILogger<SnapshotsListQueryHandler> logger)
    : IStreamQueryHandler<SnapshotsListQuery, SnapshotInfo>
{
    public async IAsyncEnumerable<SnapshotInfo> Handle(
        SnapshotsListQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // ── Stage 1: list snapshot blobs (oldest → newest) ──────────────────────
        var blobNames = await snapshots.ListBlobNamesAsync(cancellationToken);

        // ── Stage 2: resolve each manifest (disk-cache-first), yield as it resolves ──
        var count = 0;
        foreach (var blobName in blobNames)
        {
            var version  = snapshots.GetVersion(blobName);
            var manifest = await snapshots.ResolveAsync(version, cancellationToken);
            if (manifest is null)
            {
                logger.LogWarning("[snapshots] manifest for {Version} could not be resolved; skipping", version);
                continue;
            }

            count++;
            yield return new SnapshotInfo(version, manifest.Timestamp, manifest.FileCount);
        }

        logger.LogDebug("[snapshots] streamed {Count} snapshots", count);
    }
}
