using Arius.Core.Shared.Pricing;

namespace Arius.Core.Features.RestoreCommand;

/// <summary>
/// Computes the restore cost estimate shown before downloads or rehydration begin.
/// Restore-specific; storage-by-tier cost lives in <see cref="StorageCostCalculator"/>. Both read
/// their rates from the shared <see cref="PricingCatalog"/>.
/// </summary>
internal sealed class RestoreCostCalculator(RegionPricing? pricing)
{
    private readonly RegionPricing _pricing = pricing ?? PricingCatalog.LoadEmbedded().Resolve(null).Pricing;

    /// <summary>
    /// Computes a <see cref="RestoreCostEstimate"/> from classified chunk counts and byte totals.
    /// </summary>
    /// <param name="chunksAvailable">Chunk count ready for immediate download.</param>
    /// <param name="chunksAlreadyRehydrated">Archive-tier chunk count with ready rehydrated copies.</param>
    /// <param name="chunksNeedingRehydration">Archive-tier chunk count that needs rehydration.</param>
    /// <param name="chunksPendingRehydration">Archive-tier chunk count with rehydration already pending.</param>
    /// <param name="bytesNeedingRehydration">Chunk bytes that require a new rehydration request.</param>
    /// <param name="bytesPendingRehydration">Chunk bytes already pending rehydration.</param>
    /// <param name="downloadBytes">Chunk bytes available for immediate download.</param>
    /// <param name="monthsStored">Storage duration assumed for rehydrated chunk copies.</param>
    public RestoreCostEstimate Compute(
        int            chunksAvailable,
        int            chunksAlreadyRehydrated,
        int            chunksNeedingRehydration,
        int            chunksPendingRehydration,
        long           bytesNeedingRehydration,
        long           bytesPendingRehydration,
        long           downloadBytes,
        double         monthsStored = 1.0)
    {
        var numberOfBlobs = chunksNeedingRehydration;
        var totalGB       = bytesNeedingRehydration / (1024.0 * 1024.0 * 1024.0);
        var opsUnits      = numberOfBlobs / 10_000.0;

        return new RestoreCostEstimate
        {
            ChunksAvailable          = chunksAvailable,
            ChunksAlreadyRehydrated  = chunksAlreadyRehydrated,
            ChunksNeedingRehydration = chunksNeedingRehydration,
            ChunksPendingRehydration = chunksPendingRehydration,
            BytesNeedingRehydration  = bytesNeedingRehydration,
            BytesPendingRehydration  = bytesPendingRehydration,
            DownloadBytes            = downloadBytes,

            // Retrieval cost: per GB from archive
            RetrievalCostStandard = totalGB * _pricing.Archive.RetrievalPerGB,
            RetrievalCostHigh     = totalGB * _pricing.Archive.RetrievalHighPerGB,

            // Read ops: (N/10000) * rate — Azure charges per operation, not per batch
            ReadOpsCostStandard   = opsUnits * _pricing.Archive.ReadOpsPer10000,
            ReadOpsCostHigh       = opsUnits * _pricing.Archive.ReadOpsHighPer10000,

            // Write ops: (N/10000) * rate to Hot tier
            WriteOpsCost          = opsUnits * _pricing.Hot.WriteOpsPer10000,

            // Storage: N months in Hot tier (rehydrated copies in chunks-rehydrated/)
            StorageCost           = totalGB * _pricing.Hot.StoragePerGBPerMonth * monthsStored,
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

    // ── Computed totals ───────────────────────────────────────────────────────

    /// <summary>Total estimated cost at Standard priority.</summary>
    public double TotalStandard => RetrievalCostStandard + ReadOpsCostStandard + WriteOpsCost + StorageCost;

    /// <summary>Total estimated cost at High priority.</summary>
    public double TotalHigh => RetrievalCostHigh + ReadOpsCostHigh + WriteOpsCost + StorageCost;
}
