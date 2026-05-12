namespace Arius.Core.Features.RestoreCommand;

/// <summary>
/// Computes the 4-component restore cost estimate using either supplied pricing or the embedded default.
/// </summary>
internal sealed class RestoreCostCalculator
{
    private readonly PricingConfig _pricing;

    public RestoreCostCalculator(PricingConfig? pricing)
    {
        _pricing = pricing ?? PricingConfig.LoadEmbedded();
    }

    /// <summary>
    /// Computes a <see cref="RestoreCostEstimate"/> from raw inputs.
    /// </summary>
    /// <param name="chunksAvailable">Chunk count in Hot/Cool tier (ready to download).</param>
    /// <param name="chunksAlreadyRehydrated">Chunk count already in chunks-rehydrated/.</param>
    /// <param name="chunksNeedingRehydration">Chunk count in Archive, not yet rehydrated.</param>
    /// <param name="chunksPendingRehydration">Chunk count in Archive with copy in progress.</param>
    /// <param name="rehydrationBytes">Compressed bytes of chunks needing rehydration.</param>
    /// <param name="downloadBytes">Compressed bytes available for immediate download.</param>
    /// <param name="monthsStored">Storage duration assumption (default: 1 month).</param>
    public RestoreCostEstimate Compute(
        int            chunksAvailable,
        int            chunksAlreadyRehydrated,
        int            chunksNeedingRehydration,
        int            chunksPendingRehydration,
        long           rehydrationBytes,
        long           downloadBytes,
        double         monthsStored = 1.0)
    {
        var numberOfBlobs = chunksNeedingRehydration + chunksPendingRehydration;
        var totalGB       = rehydrationBytes / (1024.0 * 1024.0 * 1024.0);
        var opsUnits      = numberOfBlobs / 10_000.0;

        return new RestoreCostEstimate
        {
            ChunksAvailable          = chunksAvailable,
            ChunksAlreadyRehydrated  = chunksAlreadyRehydrated,
            ChunksNeedingRehydration = chunksNeedingRehydration,
            ChunksPendingRehydration = chunksPendingRehydration,
            RehydrationBytes         = rehydrationBytes,
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
