using Arius.Core.Restore;
using Shouldly;

namespace Arius.Core.Tests.Restore;

public class RestoreCostCalculatorTests
{
    // Deterministic pricing config for all tests
    private static readonly PricingConfig _pricing = new()
    {
        Archive = new ArchivePricingTier
        {
            RetrievalPerGB        = 1.0,
            RetrievalHighPerGB    = 5.0,
            ReadOpsPer10000       = 2.0,
            ReadOpsHighPer10000   = 10.0,
        },
        Hot  = new TierPricingConfig { WriteOpsPer10000 = 0.1, StoragePerGBPerMonth = 0.5 },
        Cool = new TierPricingConfig { WriteOpsPer10000 = 0.2, StoragePerGBPerMonth = 0.3 },
        Cold = new TierPricingConfig { WriteOpsPer10000 = 0.3, StoragePerGBPerMonth = 0.1 },
    };

    // 1 GB expressed in bytes
    private static readonly long OneGBBytes = 1L * 1024 * 1024 * 1024;

    // ── 3.4a Retrieval cost ────────────────────────────────────────────────────

    [Test]
    public void RetrievalCost_Standard_EqualsGBTimesRate()
    {
        var estimate = RestoreCostCalculator.Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            rehydrationBytes: OneGBBytes, downloadBytes: 0,
            pricing: _pricing);

        estimate.RetrievalCostStandard.ShouldBe(1.0, tolerance: 1e-9);  // 1 GB * 1.0 EUR/GB
    }

    [Test]
    public void RetrievalCost_High_EqualsGBTimesHighRate()
    {
        var estimate = RestoreCostCalculator.Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            rehydrationBytes: OneGBBytes, downloadBytes: 0,
            pricing: _pricing);

        estimate.RetrievalCostHigh.ShouldBe(5.0, tolerance: 1e-9);  // 1 GB * 5.0 EUR/GB
    }

    // ── 3.4b Read ops cost with ceil rounding ─────────────────────────────────

    [Test]
    public void ReadOpsCost_ExactBatch_NoCeiling()
    {
        // 10000 blobs → exactly 1 batch
        var estimate = RestoreCostCalculator.Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 10_000, chunksPendingRehydration: 0,
            rehydrationBytes: OneGBBytes, downloadBytes: 0,
            pricing: _pricing);

        estimate.ReadOpsCostStandard.ShouldBe(2.0, tolerance: 1e-9);  // 1 batch * 2.0
    }

    [Test]
    public void ReadOpsCost_PartialBatch_CeilsUp()
    {
        // 10001 blobs → 2 batches (ceil)
        var estimate = RestoreCostCalculator.Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 10_001, chunksPendingRehydration: 0,
            rehydrationBytes: OneGBBytes, downloadBytes: 0,
            pricing: _pricing);

        estimate.ReadOpsCostStandard.ShouldBe(4.0, tolerance: 1e-9);  // 2 batches * 2.0
    }

    [Test]
    public void ReadOpsCost_SingleBlob_IsOneBatch()
    {
        // 1 blob → ceil(1/10000) = 1 batch
        var estimate = RestoreCostCalculator.Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            rehydrationBytes: OneGBBytes, downloadBytes: 0,
            pricing: _pricing);

        estimate.ReadOpsCostStandard.ShouldBe(2.0, tolerance: 1e-9);  // 1 batch * 2.0
    }

    // ── 3.4c Write ops cost ────────────────────────────────────────────────────

    [Test]
    public void WriteOpsCost_EqualsOpsBatchesTimesHotWriteRate()
    {
        // 5000 blobs → ceil(5000/10000) = 1 batch; writeOpsPer10000 = 0.1
        var estimate = RestoreCostCalculator.Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 5_000, chunksPendingRehydration: 0,
            rehydrationBytes: OneGBBytes, downloadBytes: 0,
            pricing: _pricing);

        estimate.WriteOpsCost.ShouldBe(0.1, tolerance: 1e-9);
    }

    // ── 3.4d Storage cost ─────────────────────────────────────────────────────

    [Test]
    public void StorageCost_Default1Month_EqualsGBTimesMonthlyRate()
    {
        // 1 GB, 1 month, Hot storagePerGBPerMonth = 0.5 → 0.5
        var estimate = RestoreCostCalculator.Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            rehydrationBytes: OneGBBytes, downloadBytes: 0,
            pricing: _pricing);

        estimate.StorageCost.ShouldBe(0.5, tolerance: 1e-9);
    }

    [Test]
    public void StorageCost_CustomMonths_ScalesLinearly()
    {
        var estimate = RestoreCostCalculator.Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            rehydrationBytes: OneGBBytes, downloadBytes: 0,
            pricing: _pricing,
            monthsStored: 3.0);

        estimate.StorageCost.ShouldBe(1.5, tolerance: 1e-9);  // 0.5 * 3
    }

    // ── 3.4e Zero-chunk edge case ─────────────────────────────────────────────

    [Test]
    public void ZeroChunks_AllCostsAreZero()
    {
        var estimate = RestoreCostCalculator.Compute(
            chunksAvailable: 5, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 0, chunksPendingRehydration: 0,
            rehydrationBytes: 0, downloadBytes: OneGBBytes,
            pricing: _pricing);

        estimate.RetrievalCostStandard.ShouldBe(0.0);
        estimate.RetrievalCostHigh.ShouldBe(0.0);
        estimate.ReadOpsCostStandard.ShouldBe(0.0);
        estimate.ReadOpsCostHigh.ShouldBe(0.0);
        estimate.WriteOpsCost.ShouldBe(0.0);
        estimate.StorageCost.ShouldBe(0.0);
        estimate.TotalStandard.ShouldBe(0.0);
        estimate.TotalHigh.ShouldBe(0.0);
    }

    // ── 3.4f Computed totals ──────────────────────────────────────────────────

    [Test]
    public void TotalStandard_SumsAllComponents()
    {
        // 1 GB, 1 blob → opsBatches=1
        // Standard total = 1*1.0 + 1*2.0 + 1*0.1 + 1*0.5 = 3.6
        var estimate = RestoreCostCalculator.Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            rehydrationBytes: OneGBBytes, downloadBytes: 0,
            pricing: _pricing);

        estimate.TotalStandard.ShouldBe(3.6, tolerance: 1e-9);
    }

    [Test]
    public void TotalHigh_SumsAllComponents()
    {
        // High total = 1*5.0 + 1*10.0 + 1*0.1 + 1*0.5 = 15.6
        var estimate = RestoreCostCalculator.Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1, chunksPendingRehydration: 0,
            rehydrationBytes: OneGBBytes, downloadBytes: 0,
            pricing: _pricing);

        estimate.TotalHigh.ShouldBe(15.6, tolerance: 1e-9);
    }

    [Test]
    public void TotalHigh_AlwaysGreaterThanOrEqualToTotalStandard()
    {
        var estimate = RestoreCostCalculator.Compute(
            chunksAvailable: 0, chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 5, chunksPendingRehydration: 3,
            rehydrationBytes: 2 * OneGBBytes, downloadBytes: OneGBBytes,
            pricing: _pricing);

        estimate.TotalHigh.ShouldBeGreaterThanOrEqualTo(estimate.TotalStandard);
    }
}
