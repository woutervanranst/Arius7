using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Snapshot;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Arius.Core.Features.StatisticsQuery;

// --- QUERY

/// <summary>
/// Mediator command: aggregate repository statistics for the Statistics view.
/// </summary>
/// <param name="Version">Snapshot version (partial match). <c>null</c> = latest.</param>
public sealed record StatisticsQuery(string? Version = null) : ICommand<RepositoryStatistics>;

// --- RESULT

/// <summary>
/// Repository statistics.
/// </summary>
/// <param name="Files">Number of files in the snapshot (from the manifest; per-snapshot).</param>
/// <param name="OriginalSize">
/// Logical size: sum of original (uncompressed) file sizes in bytes, counting duplicates once per file
/// (from the manifest; per-snapshot). This is the size you would restore.
/// </param>
/// <param name="DeduplicatedSize">
/// Sum of original (uncompressed) sizes over distinct content — the unique data before compression
/// (from the chunk index; repository-wide across all snapshots).
/// </param>
/// <param name="StoredSize">
/// Sum of stored chunk sizes over distinct chunks — the actual cloud storage footprint, deduplicated
/// and compressed (from the chunk index; repository-wide across all snapshots).
/// </param>
/// <param name="UniqueChunks">Number of distinct chunks (from the chunk index; repository-wide).</param>
/// <param name="StoredByTier">Distinct-chunk count and stored size split by storage tier (repository-wide).</param>
/// <remarks>
/// An empty repository (no snapshot yet) reports all-zero figures. <see cref="Files"/> and
/// <see cref="OriginalSize"/> are scoped to the resolved snapshot; the deduplicated/stored/chunk figures
/// are read straight from the local chunk-index cache (no blob reads) and are repository-wide — they
/// reflect the cache's current coverage and finalise once it has fully synchronised.
/// </remarks>
public sealed record RepositoryStatistics(
    long Files,
    long OriginalSize,
    long DeduplicatedSize,
    long StoredSize,
    long UniqueChunks,
    IReadOnlyList<ChunkTierStatistic> StoredByTier);

// --- HANDLER

/// <summary>
/// Combines the snapshot manifest totals (files, original size) with the chunk-index aggregate
/// (stored size, unique chunks).
/// </summary>
public sealed class StatisticsQueryHandler(
    ISnapshotService          snapshots,
    IChunkIndexService        chunkIndex,
    ILogger<StatisticsQueryHandler> logger)
    : ICommandHandler<StatisticsQuery, RepositoryStatistics>
{
    public async ValueTask<RepositoryStatistics> Handle(StatisticsQuery query, CancellationToken cancellationToken)
    {
        // ── Stage 1: manifest totals (files, original size) ─────────────────────
        var snapshot = await snapshots.ResolveAsync(query.Version, cancellationToken);
        if (snapshot is null)
        {
            logger.LogDebug("[stats] no snapshot for version {Version}; returning empty stats", query.Version ?? "<latest>");
            return new RepositoryStatistics(0, 0, 0, 0, 0, []);
        }

        // ── Stage 2: chunk-index aggregate over distinct chunks, split by storage tier ──
        var byTier = chunkIndex.GetStatistics();

        return new RepositoryStatistics(
            Files:            snapshot.FileCount,
            OriginalSize:     snapshot.OriginalSize,
            DeduplicatedSize: chunkIndex.GetDeduplicatedOriginalSize(),
            StoredSize:       byTier.Sum(t => t.StoredSize),
            UniqueChunks:     byTier.Sum(t => t.UniqueChunks),
            StoredByTier:     byTier);
    }
}
