using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Snapshot;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.StatsQuery;

// --- QUERY

/// <summary>
/// Mediator command: aggregate repository statistics for the Statistics view.
/// </summary>
/// <param name="Version">Snapshot version (partial match). <c>null</c> = latest.</param>
public sealed record StatsQuery(string? Version = null) : ICommand<RepositoryStats>;

// --- RESULT

/// <summary>
/// Repository statistics.
/// </summary>
/// <param name="Files">Number of files in the snapshot (from the manifest).</param>
/// <param name="OriginalSize">Sum of original (uncompressed) file sizes in bytes (from the manifest).</param>
/// <param name="StoredSize">Sum of stored chunk sizes over distinct chunks (from the chunk index).</param>
/// <param name="UniqueChunks">Number of distinct chunks (from the chunk index).</param>
/// <remarks>
/// An empty repository (no snapshot yet) reports all-zero figures. Stored/unique-chunk figures are
/// otherwise computed exactly: the query loads every chunk-index shard (a bounded set, ≤256 small
/// index blobs — not the chunk data) before aggregating, so the response blocks until the figures
/// are final rather than streaming a partial result.
/// </remarks>
public sealed record RepositoryStats(
    long Files,
    long OriginalSize,
    long StoredSize,
    long UniqueChunks);

// --- HANDLER

/// <summary>
/// Combines the snapshot manifest totals (files, original size) with the chunk-index aggregate
/// (stored size, unique chunks).
/// </summary>
public sealed class StatsQueryHandler(
    ISnapshotService          snapshots,
    IChunkIndexService        chunkIndex,
    ILogger<StatsQueryHandler> logger)
    : ICommandHandler<StatsQuery, RepositoryStats>
{
    public async ValueTask<RepositoryStats> Handle(StatsQuery query, CancellationToken cancellationToken)
    {
        // ── Stage 1: manifest totals (files, original size) ─────────────────────
        var manifest = await snapshots.ResolveAsync(query.Version, cancellationToken);
        if (manifest is null)
        {
            logger.LogDebug("[stats] no snapshot for version {Version}; returning empty stats", query.Version ?? "<latest>");
            return new RepositoryStats(0, 0, 0, 0);
        }

        // ── Stage 2: chunk-index aggregate over distinct chunks (stored size, unique chunks) ──
        var (uniqueChunks, storedSize) = await chunkIndex.GetStatsAsync(cancellationToken);

        return new RepositoryStats(
            Files:        manifest.FileCount,
            OriginalSize: manifest.TotalSize,
            StoredSize:   storedSize,
            UniqueChunks: uniqueChunks);
    }
}
