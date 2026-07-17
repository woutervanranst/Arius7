using Arius.Core.Shared.Cost;

namespace Arius.AzureBlob.Pricing;

/// <summary>
/// Computes the detailed Azure restore cost from a <see cref="RestoreCostRequest"/> and a region's rates.
/// Two cost groups:
/// <list type="bullet">
///   <item><b>Archive rehydration</b> — chunks in the offline archive tier are rehydrated first (per-GiB
///   retrieval + read ops, Standard/High), then Arius copies them into Hot (write ops + Hot storage).</item>
///   <item><b>Online download</b> — chunks in Hot/Cool/Cold (plus already-rehydrated copies, read from Hot)
///   are read directly: read ops on every chunk + a per-GiB retrieval charge on Cool/Cold (Hot has none).</item>
/// </list>
/// Plus internet egress on ALL bytes that leave Azure (download + rehydrated). Azure's first 100 GiB/month of
/// egress is free account-wide, but that allowance is shared across every transfer in the billing month and
/// prior runs consume an unknowable share — so we conservatively bill egress from the first byte (deliberately
/// an over-estimate) rather than assume free capacity remains.
/// </summary>
internal static class AzureRestoreCostCalculator
{
    // Azure bills per-GB storage/retrieval/egress as binary GiB (2^30 bytes).
    private const double BytesPerGiB = 1024.0 * 1024.0 * 1024.0;

    public static AzureRestoreCost Compute(RegionPricing pricing, RestoreCostRequest r)
    {
        // Archive rehydration (offline → online).
        var rehydGiB = r.BytesNeedingRehydration / BytesPerGiB;
        var rehydOps = r.ChunksNeedingRehydration / 10_000.0;

        // Online download: read ops on every chunk + per-GiB retrieval on Cool/Cold (Hot is free).
        var downloadReadOps =
            r.HotDownloadChunks  / 10_000.0 * pricing.ReadOpsRateFor(BlobTier.Hot)  +
            r.CoolDownloadChunks / 10_000.0 * pricing.ReadOpsRateFor(BlobTier.Cool) +
            r.ColdDownloadChunks / 10_000.0 * pricing.ReadOpsRateFor(BlobTier.Cold);
        var downloadRetrieval =
            r.CoolDownloadBytes / BytesPerGiB * pricing.DataRetrievalRateFor(BlobTier.Cool) +
            r.ColdDownloadBytes / BytesPerGiB * pricing.DataRetrievalRateFor(BlobTier.Cold);

        // Internet egress on the bytes leaving Azure (download now + the archive bytes we rehydrate then download).
        // Billed from the first byte — see the class remarks on why the free monthly allowance is not assumed.
        var egressGiB  = (r.DownloadBytes + r.BytesNeedingRehydration) / BytesPerGiB;
        var egressCost = egressGiB * pricing.EgressPerGb;

        return new AzureRestoreCost
        {
            RetrievalCostStandard = rehydGiB * pricing.DataRetrievalRateFor(BlobTier.Archive),
            RetrievalCostHigh     = rehydGiB * pricing.DataRetrievalRateFor(BlobTier.Archive, highPriority: true),
            ReadOpsCostStandard   = rehydOps * pricing.ReadOpsRateFor(BlobTier.Archive),
            ReadOpsCostHigh       = rehydOps * pricing.ReadOpsRateFor(BlobTier.Archive, highPriority: true),
            WriteOpsCost          = rehydOps * pricing.WriteOpsRateFor(BlobTier.Hot),   // copy rehydrated → Hot
            StorageCost           = rehydGiB * pricing.StorageRateFor(BlobTier.Hot) * r.MonthsStored,
            DownloadReadOpsCost   = downloadReadOps,
            DownloadRetrievalCost = downloadRetrieval,
            EgressCost            = egressCost,
        };
    }
}

/// <summary>Detailed Azure restore cost components (an Azure implementation detail; collapsed to totals in the canonical estimate).</summary>
internal sealed record AzureRestoreCost
{
    public double RetrievalCostStandard { get; init; }
    public double RetrievalCostHigh     { get; init; }
    public double ReadOpsCostStandard   { get; init; }
    public double ReadOpsCostHigh       { get; init; }
    public double WriteOpsCost          { get; init; }
    public double StorageCost           { get; init; }
    public double DownloadReadOpsCost   { get; init; }
    public double DownloadRetrievalCost { get; init; }
    public double EgressCost            { get; init; }

    public double TotalStandard => RetrievalCostStandard + ReadOpsCostStandard + WriteOpsCost + StorageCost + DownloadReadOpsCost + DownloadRetrievalCost + EgressCost;
    public double TotalHigh      => RetrievalCostHigh     + ReadOpsCostHigh     + WriteOpsCost + StorageCost + DownloadReadOpsCost + DownloadRetrievalCost + EgressCost;
}
