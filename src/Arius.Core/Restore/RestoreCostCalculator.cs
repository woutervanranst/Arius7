namespace Arius.Core.Restore;

/// <summary>
/// Static helpers for computing the 4-component restore cost estimate.
/// Extracted for unit testability.
/// </summary>
public static class RestoreCostCalculator
{
    /// <summary>
    /// Computes a <see cref="RestoreCostEstimate"/> from raw inputs.
    /// </summary>
    /// <param name="chunksAvailable">Chunk count in Hot/Cool tier (ready to download).</param>
    /// <param name="chunksAlreadyRehydrated">Chunk count already in chunks-rehydrated/.</param>
    /// <param name="chunksNeedingRehydration">Chunk count in Archive, not yet rehydrated.</param>
    /// <param name="chunksPendingRehydration">Chunk count in Archive with copy in progress.</param>
    /// <param name="rehydrationBytes">Compressed bytes of chunks needing rehydration.</param>
    /// <param name="downloadBytes">Compressed bytes available for immediate download.</param>
    /// <param name="pricing">Pricing config to use (defaults to <see cref="PricingConfig.Load"/>).</param>
    /// <param name="monthsStored">Storage duration assumption (default: 1 month).</param>
    public static RestoreCostEstimate Compute(
        int            chunksAvailable,
        int            chunksAlreadyRehydrated,
        int            chunksNeedingRehydration,
        int            chunksPendingRehydration,
        long           rehydrationBytes,
        long           downloadBytes,
        PricingConfig? pricing      = null,
        double         monthsStored = 1.0)
    {
        pricing ??= PricingConfig.Load();

        var numberOfBlobs = chunksNeedingRehydration + chunksPendingRehydration;
        var totalGB       = rehydrationBytes / (1024.0 * 1024.0 * 1024.0);
        var opsBatches    = numberOfBlobs == 0 ? 0 : (int)Math.Ceiling(numberOfBlobs / 10_000.0);

        return new RestoreCostEstimate
        {
            ChunksAvailable          = chunksAvailable,
            ChunksAlreadyRehydrated  = chunksAlreadyRehydrated,
            ChunksNeedingRehydration = chunksNeedingRehydration,
            ChunksPendingRehydration = chunksPendingRehydration,
            RehydrationBytes         = rehydrationBytes,
            DownloadBytes            = downloadBytes,

            // Retrieval cost: per GB from archive
            RetrievalCostStandard = totalGB * pricing.Archive.RetrievalPerGB,
            RetrievalCostHigh     = totalGB * pricing.Archive.RetrievalHighPerGB,

            // Read ops: ceil(N/10000) batches on archive blobs
            ReadOpsCostStandard   = opsBatches * pricing.Archive.ReadOpsPer10000,
            ReadOpsCostHigh       = opsBatches * pricing.Archive.ReadOpsHighPer10000,

            // Write ops: ceil(N/10000) batches to Hot tier
            WriteOpsCost          = opsBatches * pricing.Hot.WriteOpsPer10000,

            // Storage: N months in Hot tier (rehydrated copies in chunks-rehydrated/)
            StorageCost           = totalGB * pricing.Hot.StoragePerGBPerMonth * monthsStored,
        };
    }
}
