using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Pricing;

namespace Arius.Core.Tests.Features.RestoreCommand;

public class RestoreCostCalculatorTests
{
    // Deterministic pricing config for all tests
    private static readonly RegionPricing _pricing = new()
    {
        EgressPerGb = 0.08,
        Archive = new TierRates
        {
            DataRetrievalPerGb     = 1.0,
            DataRetrievalHighPerGb = 5.0,
            ReadOpsPer10k          = 2.0,
            ReadOpsHighPer10k      = 10.0,
        },
        Hot  = new TierRates { WriteOpsPer10k = 0.1, StoragePerGbMonth = 0.5, ReadOpsPer10k = 0.04 },
        Cool = new TierRates { WriteOpsPer10k = 0.2, StoragePerGbMonth = 0.3, ReadOpsPer10k = 0.05, DataRetrievalPerGb = 0.6 },
        Cold = new TierRates { WriteOpsPer10k = 0.3, StoragePerGbMonth = 0.1, ReadOpsPer10k = 0.07, DataRetrievalPerGb = 0.9 },
    };

    // 1 GB expressed in bytes
    private static readonly long OneGBBytes = 1L * 1024 * 1024 * 1024;

    [Test]
    public void Constructor_WhenPricingProvided_UsesSuppliedConfig()
    {
        var calculator = new RestoreCostCalculator(_pricing);

        var estimate = calculator.Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes, bytesPendingRehydration: 0, downloadBytes: 0);

        estimate.RetrievalCostStandard.ShouldBe(1.0, tolerance: 1e-9);
    }

    [Test]
    public void Constructor_WhenPricingIsNull_UsesEmbeddedConfig()
    {
        var calculator = new RestoreCostCalculator(pricing: null);

        var estimate = calculator.Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes, bytesPendingRehydration: 0, downloadBytes: 0);

        estimate.RetrievalCostStandard.ShouldBeGreaterThan(0);
        estimate.RetrievalCostHigh.ShouldBeGreaterThan(estimate.RetrievalCostStandard);
    }

    // ── 3.4a Retrieval cost ────────────────────────────────────────────────────

    [Test]
    public void RetrievalCost_Standard_EqualsGBTimesRate()
    {
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes, bytesPendingRehydration: 0, downloadBytes: 0);

        estimate.RetrievalCostStandard.ShouldBe(1.0, tolerance: 1e-9); // 1 GB * 1.0 EUR/GB
    }

    [Test]
    public void RetrievalCost_High_EqualsGBTimesHighRate()
    {
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes, bytesPendingRehydration: 0, downloadBytes: 0);

        estimate.RetrievalCostHigh.ShouldBe(5.0, tolerance: 1e-9);  // 1 GB * 5.0 EUR/GB
    }

    [Test]
    public void ReadOpsCost_10000Blobs_EqualsRate()
    {
        // 10000 blobs → (10000/10000) * 2.0 = 2.0
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 10_000, chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes, bytesPendingRehydration: 0, downloadBytes: 0);

        estimate.ReadOpsCostStandard.ShouldBe(2.0, tolerance: 1e-9);
    }

    [Test]
    public void ReadOpsCost_ScalesLinearly()
    {
        // 10001 blobs → (10001/10000) * 2.0 = 2.0002
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 10_001, chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes, bytesPendingRehydration: 0, downloadBytes: 0);

        estimate.ReadOpsCostStandard.ShouldBe(2.0002, tolerance: 1e-9);
    }

    [Test]
    public void ReadOpsCost_SingleBlob_FractionalUnit()
    {
        // 1 blob → (1/10000) * 2.0 = 0.0002
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes, bytesPendingRehydration: 0, downloadBytes: 0);

        estimate.ReadOpsCostStandard.ShouldBe(0.0002, tolerance: 1e-9);
    }

    [Test]
    public void WriteOpsCost_ScalesProportionally()
    {
        // 5000 blobs → (5000/10000) * 0.1 = 0.05
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 5_000, chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes, bytesPendingRehydration: 0, downloadBytes: 0);

        estimate.WriteOpsCost.ShouldBe(0.05, tolerance: 1e-9);
    }

    [Test]
    public void StorageCost_Default1Month_EqualsGBTimesMonthlyRate()
    {
        // 1 GB, 1 month, Hot storagePerGBPerMonth = 0.5 → 0.5
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes, bytesPendingRehydration: 0, downloadBytes: 0);

        estimate.StorageCost.ShouldBe(0.5, tolerance: 1e-9); // 0.5 * 3
    }

    [Test]
    public void StorageCost_CustomMonths_ScalesLinearly()
    {
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes, bytesPendingRehydration: 0, downloadBytes: 0,
            monthsStored: 3.0);

        estimate.StorageCost.ShouldBe(1.5, tolerance: 1e-9);
    }

    [Test]
    public void ZeroChunks_AllCostsAreZero()
    {
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 5, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 0, chunksPendingRehydration: 0,
            bytesNeedingRehydration: 0, bytesPendingRehydration: 0, downloadBytes: OneGBBytes);

        estimate.RetrievalCostStandard.ShouldBe(0.0);
        estimate.RetrievalCostHigh.ShouldBe(0.0);
        estimate.ReadOpsCostStandard.ShouldBe(0.0);
        estimate.ReadOpsCostHigh.ShouldBe(0.0);
        estimate.WriteOpsCost.ShouldBe(0.0);
        estimate.StorageCost.ShouldBe(0.0);
        estimate.TotalStandard.ShouldBe(0.0);
        estimate.TotalHigh.ShouldBe(0.0);
    }

    [Test]
    public void PendingOnlyChunks_ReportPendingBytesButDoNotAddRehydrationCost()
    {
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 0, chunksPendingRehydration: 1,
            bytesNeedingRehydration: 0, bytesPendingRehydration: OneGBBytes, downloadBytes: 0);

        estimate.BytesNeedingRehydration.ShouldBe(0);
        estimate.BytesPendingRehydration.ShouldBe(OneGBBytes);
        estimate.RetrievalCostStandard.ShouldBe(0.0);
        estimate.RetrievalCostHigh.ShouldBe(0.0);
        estimate.ReadOpsCostStandard.ShouldBe(0.0);
        estimate.ReadOpsCostHigh.ShouldBe(0.0);
        estimate.WriteOpsCost.ShouldBe(0.0);
        estimate.StorageCost.ShouldBe(0.0);
    }

    [Test]
    public void TotalStandard_SumsAllComponents()
    {
        // 1 GB, 1 blob → opsUnits = 1/10000 = 0.0001
        // Standard total = 1*1.0 + 0.0001*2.0 + 0.0001*0.1 + 1*0.5 = 1.50021
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes, bytesPendingRehydration: 0, downloadBytes: 0);

        estimate.TotalStandard.ShouldBe(1.50021, tolerance: 1e-9);
    }

    [Test]
    public void TotalHigh_SumsAllComponents()
    {
        // High total = 1*5.0 + 0.0001*10.0 + 0.0001*0.1 + 1*0.5 = 5.50101
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes, bytesPendingRehydration: 0, downloadBytes: 0);

        estimate.TotalHigh.ShouldBe(5.50101, tolerance: 1e-9);
    }

    [Test]
    public void TotalHigh_AlwaysGreaterThanOrEqualToTotalStandard()
    {
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 5, chunksPendingRehydration: 3,
            bytesNeedingRehydration: 2 * OneGBBytes, bytesPendingRehydration: 0, downloadBytes: OneGBBytes);

        estimate.TotalHigh.ShouldBeGreaterThanOrEqualTo(estimate.TotalStandard);
    }

    // ── Online download cost (read ops per tier + Cool/Cold data retrieval) ─────

    [Test]
    public void DownloadCost_ReadOpsPerTier_PlusCoolColdRetrieval()
    {
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 30_000, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 0, chunksPendingRehydration: 0,
            bytesNeedingRehydration: 0, bytesPendingRehydration: 0, downloadBytes: 3 * OneGBBytes,
            hotDownloadChunks: 10_000,  hotDownloadBytes:  OneGBBytes,
            coolDownloadChunks: 10_000, coolDownloadBytes: OneGBBytes,
            coldDownloadChunks: 10_000, coldDownloadBytes: OneGBBytes);

        // Read ops: 1*0.04 (hot) + 1*0.05 (cool) + 1*0.07 (cold) = 0.16
        estimate.DownloadReadOpsCost.ShouldBe(0.16, tolerance: 1e-9);
        // Retrieval: hot none + 1 GiB*0.6 (cool) + 1 GiB*0.9 (cold) = 1.5
        estimate.DownloadRetrievalCost.ShouldBe(1.5, tolerance: 1e-9);
        // No archive rehydration → those components are zero.
        estimate.RetrievalCostStandard.ShouldBe(0.0);
        estimate.WriteOpsCost.ShouldBe(0.0);
        estimate.StorageCost.ShouldBe(0.0);
        // Download cost is independent of rehydration priority.
        estimate.TotalStandard.ShouldBe(1.66, tolerance: 1e-9);
        estimate.TotalHigh.ShouldBe(1.66, tolerance: 1e-9);
    }

    [Test]
    public void DownloadCost_HotTier_HasNoRetrievalCharge()
    {
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 10_000, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 0, chunksPendingRehydration: 0,
            bytesNeedingRehydration: 0, bytesPendingRehydration: 0, downloadBytes: OneGBBytes,
            hotDownloadChunks: 10_000, hotDownloadBytes: OneGBBytes);

        estimate.DownloadRetrievalCost.ShouldBe(0.0); // Hot has no per-GiB retrieval charge
        estimate.DownloadReadOpsCost.ShouldBe(0.04, tolerance: 1e-9);
    }

    // ── Internet egress (first 100 GiB/month free) ──────────────────────────────

    [Test]
    public void EgressCost_BeyondFreeAllowance_ChargedPerGiB()
    {
        // 200 GiB restored → 100 GiB billable (first 100 free) × 0.08 = 8.0
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 0, chunksPendingRehydration: 0,
            bytesNeedingRehydration: 0, bytesPendingRehydration: 0,
            downloadBytes: 200L * 1024 * 1024 * 1024);

        estimate.EgressCost.ShouldBe(8.0, tolerance: 1e-9);
        estimate.TotalStandard.ShouldBe(8.0, tolerance: 1e-9);
    }

    [Test]
    public void EgressCost_WithinFreeAllowance_IsZero()
    {
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 0, chunksPendingRehydration: 0,
            bytesNeedingRehydration: 0, bytesPendingRehydration: 0,
            downloadBytes: 50L * 1024 * 1024 * 1024);

        estimate.EgressCost.ShouldBe(0.0);
    }

    [Test]
    public void EgressCost_CountsRehydratedArchiveBytes_NotPending()
    {
        // 80 GiB online + 60 GiB archive-to-rehydrate = 140 GiB egress → 40 GiB billable × 0.08 = 3.2.
        // Pending bytes are excluded (not downloaded in this run).
        var estimate = new RestoreCostCalculator(_pricing).Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 1,
            bytesNeedingRehydration: 60L * 1024 * 1024 * 1024,
            bytesPendingRehydration: 500L * 1024 * 1024 * 1024,
            downloadBytes: 80L * 1024 * 1024 * 1024);

        estimate.EgressCost.ShouldBe(3.2, tolerance: 1e-9);
    }
}
