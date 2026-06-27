using Arius.Core.Shared.Pricing;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Features.RestoreCommand;

/// <summary>
/// Computes the restore cost estimate shown before downloads or rehydration begin. Restore-specific;
/// storage-by-tier monthly cost lives in <see cref="StorageCostCalculator"/>. Both read their rates from
/// the shared region-aware <see cref="PricingCatalog"/>.
/// </summary>
/// <remarks>
/// Two cost groups:
/// <list type="bullet">
///   <item><b>Archive rehydration</b> — chunks in the offline archive tier must be rehydrated first: per-GiB
///   data retrieval + read operations (Standard or High priority), then Arius copies them into the Hot tier
///   (chunks-rehydrated/), adding Hot write operations + Hot storage for the copies.</item>
///   <item><b>Online download</b> — chunks already in an online tier (Hot/Cool/Cold, plus archive copies that
///   are already rehydrated) are read directly: read operations on every chunk + a per-GiB data-retrieval
///   charge on Cool and Cold (Hot has none).</item>
/// </list>
/// </remarks>
internal sealed class RestoreCostCalculator(RegionPricing? pricing)
{
    // Azure bills per-GB storage and retrieval as binary GiB (2^30 bytes).
    private const double BytesPerGiB = 1024.0 * 1024.0 * 1024.0;

    private readonly RegionPricing _pricing = pricing ?? PricingCatalog.LoadEmbedded().Resolve(null).Pricing;

    /// <summary>
    /// Computes a <see cref="RestoreCostEstimate"/> from classified chunk counts and byte totals. The
    /// per-tier <c>*Download*</c> parameters describe the chunks read directly from online tiers (an
    /// already-rehydrated archive copy counts as Hot); omitting them yields no download cost.
    /// </summary>
    public RestoreCostEstimate Compute(
        int            chunksAvailable,
        int            chunksAlreadyRehydrated,
        int            chunksNeedingRehydration,
        int            chunksPendingRehydration,
        long           bytesNeedingRehydration,
        long           bytesPendingRehydration,
        long           downloadBytes,
        double         monthsStored        = 1.0,
        int            hotDownloadChunks   = 0, long hotDownloadBytes  = 0,
        int            coolDownloadChunks  = 0, long coolDownloadBytes = 0,
        int            coldDownloadChunks  = 0, long coldDownloadBytes = 0)
    {
        // ── Archive rehydration (offline → online) ──
        var rehydGiB = bytesNeedingRehydration / BytesPerGiB;
        var rehydOps = chunksNeedingRehydration / 10_000.0;

        // ── Online download: read ops on every chunk + per-GiB retrieval on Cool/Cold (Hot is free) ──
        var downloadReadOps =
            hotDownloadChunks  / 10_000.0 * _pricing.ReadOpsRateFor(BlobTier.Hot)  +
            coolDownloadChunks / 10_000.0 * _pricing.ReadOpsRateFor(BlobTier.Cool) +
            coldDownloadChunks / 10_000.0 * _pricing.ReadOpsRateFor(BlobTier.Cold);
        var downloadRetrieval =
            coolDownloadBytes / BytesPerGiB * _pricing.DataRetrievalRateFor(BlobTier.Cool) +
            coldDownloadBytes / BytesPerGiB * _pricing.DataRetrievalRateFor(BlobTier.Cold);

        return new RestoreCostEstimate
        {
            ChunksAvailable          = chunksAvailable,
            ChunksAlreadyRehydrated  = chunksAlreadyRehydrated,
            ChunksNeedingRehydration = chunksNeedingRehydration,
            ChunksPendingRehydration = chunksPendingRehydration,
            BytesNeedingRehydration  = bytesNeedingRehydration,
            BytesPendingRehydration  = bytesPendingRehydration,
            DownloadBytes            = downloadBytes,

            // Archive rehydration — per-GiB data retrieval at Standard / High priority.
            RetrievalCostStandard = rehydGiB * _pricing.DataRetrievalRateFor(BlobTier.Archive),
            RetrievalCostHigh     = rehydGiB * _pricing.DataRetrievalRateFor(BlobTier.Archive, highPriority: true),

            // Archive read ops — (N/10000) * rate at Standard / High priority.
            ReadOpsCostStandard   = rehydOps * _pricing.ReadOpsRateFor(BlobTier.Archive),
            ReadOpsCostHigh       = rehydOps * _pricing.ReadOpsRateFor(BlobTier.Archive, highPriority: true),

            // Arius copies each rehydrated chunk into the Hot tier (chunks-rehydrated/): write ops + storage.
            WriteOpsCost          = rehydOps * _pricing.WriteOpsRateFor(BlobTier.Hot),
            StorageCost           = rehydGiB * _pricing.StorageRateFor(BlobTier.Hot) * monthsStored,

            // Direct download from online tiers.
            DownloadReadOpsCost   = downloadReadOps,
            DownloadRetrievalCost = downloadRetrieval,
        };
    }
}


// --- COST ESTIMATE

/// <summary>
/// Restore cost estimate presented before downloads or rehydration begin.
/// Monetary values use the currency configured in <c>pricing.json</c> (default: EUR).
/// </summary>
public sealed record RestoreCostEstimate
{
    // ── Chunk availability counts ─────────────────────────────────────────────

    /// <summary>Chunks available for immediate download.</summary>
    public required int ChunksAvailable { get; init; }

    /// <summary>Archive-tier chunks with ready rehydrated copies.</summary>
    public required int ChunksAlreadyRehydrated { get; init; }

    /// <summary>Archive-tier chunks that need rehydration.</summary>
    public required int ChunksNeedingRehydration { get; init; }

    /// <summary>Archive-tier chunks with rehydration already pending.</summary>
    public required int ChunksPendingRehydration { get; init; }

    /// <summary>Total chunk bytes that require a new rehydration request.</summary>
    public required long BytesNeedingRehydration { get; init; }

    /// <summary>Total chunk bytes already pending rehydration.</summary>
    public required long BytesPendingRehydration { get; init; }

    /// <summary>Total chunk bytes available for immediate download.</summary>
    public required long DownloadBytes { get; init; }

    // ── Per-component cost fields ─────────────────────────────────────────────

    /// <summary>Archive data retrieval cost at Standard priority.</summary>
    public required double RetrievalCostStandard { get; init; }

    /// <summary>Archive data retrieval cost at High priority.</summary>
    public required double RetrievalCostHigh { get; init; }

    /// <summary>Archive read operation cost at Standard priority.</summary>
    public required double ReadOpsCostStandard { get; init; }

    /// <summary>Archive read operation cost at High priority.</summary>
    public required double ReadOpsCostHigh { get; init; }

    /// <summary>Write operation cost for rehydrated chunk copies.</summary>
    public required double WriteOpsCost { get; init; }

    /// <summary>Storage cost for rehydrated chunk copies.</summary>
    public required double StorageCost { get; init; }

    /// <summary>Read operation cost for chunks downloaded directly from online tiers (Hot/Cool/Cold + rehydrated copies).</summary>
    public double DownloadReadOpsCost { get; init; }

    /// <summary>Per-GiB data-retrieval cost for chunks downloaded from the Cool and Cold tiers.</summary>
    public double DownloadRetrievalCost { get; init; }

    // ── Computed totals ───────────────────────────────────────────────────────

    /// <summary>Total estimated cost at Standard priority.</summary>
    public double TotalStandard => RetrievalCostStandard + ReadOpsCostStandard + WriteOpsCost + StorageCost + DownloadReadOpsCost + DownloadRetrievalCost;

    /// <summary>Total estimated cost at High priority.</summary>
    public double TotalHigh => RetrievalCostHigh + ReadOpsCostHigh + WriteOpsCost + StorageCost + DownloadReadOpsCost + DownloadRetrievalCost;
}
