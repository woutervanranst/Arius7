using Arius.AzureBlob.Pricing;
using Arius.Core.Shared.Cost;

namespace Arius.AzureBlob.Tests.Pricing;

public class AzureRestoreCostCalculatorTests
{
    // Deterministic synthetic rates for all tests.
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

    private static readonly long OneGBBytes = 1L * 1024 * 1024 * 1024;

    private static AzureRestoreCost Rehydrate(int chunks, long bytes, double monthsStored = 1.0) =>
        AzureRestoreCostCalculator.Compute(_pricing, new RestoreCostRequest
        {
            ChunksNeedingRehydration = chunks,
            BytesNeedingRehydration  = bytes,
            MonthsStored             = monthsStored,
        });

    // ── Archive rehydration components ──────────────────────────────────────────

    [Test]
    public void RetrievalCost_Standard_EqualsGiBTimesRate()
        => Rehydrate(1, OneGBBytes).RetrievalCostStandard.ShouldBe(1.0, tolerance: 1e-9);

    [Test]
    public void RetrievalCost_High_EqualsGiBTimesHighRate()
        => Rehydrate(1, OneGBBytes).RetrievalCostHigh.ShouldBe(5.0, tolerance: 1e-9);

    [Test]
    public void ReadOpsCost_10000Blobs_EqualsRate()
        => Rehydrate(10_000, OneGBBytes).ReadOpsCostStandard.ShouldBe(2.0, tolerance: 1e-9);

    [Test]
    public void ReadOpsCost_SingleBlob_FractionalUnit()
        => Rehydrate(1, OneGBBytes).ReadOpsCostStandard.ShouldBe(0.0002, tolerance: 1e-9);

    [Test]
    public void WriteOpsCost_ScalesProportionally()
        => Rehydrate(5_000, OneGBBytes).WriteOpsCost.ShouldBe(0.05, tolerance: 1e-9); // 0.5*0.1

    [Test]
    public void StorageCost_Default1Month_EqualsGiBTimesMonthlyRate()
        => Rehydrate(1, OneGBBytes).StorageCost.ShouldBe(0.5, tolerance: 1e-9);

    [Test]
    public void StorageCost_CustomMonths_ScalesLinearly()
        => Rehydrate(1, OneGBBytes, monthsStored: 3.0).StorageCost.ShouldBe(1.5, tolerance: 1e-9);

    [Test]
    public void TotalStandard_SumsAllComponents()
        => Rehydrate(1, OneGBBytes).TotalStandard.ShouldBe(1.50021, tolerance: 1e-9); // 1 + 0.0002 + 0.00001 + 0.5

    [Test]
    public void TotalHigh_SumsAllComponents()
        => Rehydrate(1, OneGBBytes).TotalHigh.ShouldBe(5.50101, tolerance: 1e-9); // 5 + 0.001 + 0.00001 + 0.5

    [Test]
    public void ZeroChunks_AllCostsAreZero()
    {
        var cost = AzureRestoreCostCalculator.Compute(_pricing, new RestoreCostRequest { ChunksAvailable = 5, DownloadBytes = OneGBBytes });
        cost.RetrievalCostStandard.ShouldBe(0.0);
        cost.ReadOpsCostStandard.ShouldBe(0.0);
        cost.WriteOpsCost.ShouldBe(0.0);
        cost.StorageCost.ShouldBe(0.0);
        cost.TotalStandard.ShouldBe(0.0); // 1 GiB download with no per-tier breakdown → 0
        cost.TotalHigh.ShouldBe(0.0);
    }

    [Test]
    public void PendingOnly_AddsNoCost()
    {
        var cost = AzureRestoreCostCalculator.Compute(_pricing, new RestoreCostRequest { ChunksPendingRehydration = 1, BytesPendingRehydration = OneGBBytes });
        cost.TotalStandard.ShouldBe(0.0);
        cost.EgressCost.ShouldBe(0.0); // pending bytes are excluded from egress
    }

    [Test]
    public void TotalHigh_AlwaysGreaterThanOrEqualToTotalStandard()
    {
        var cost = AzureRestoreCostCalculator.Compute(_pricing, new RestoreCostRequest
        {
            ChunksNeedingRehydration = 5, ChunksPendingRehydration = 3,
            BytesNeedingRehydration = 2 * OneGBBytes, DownloadBytes = OneGBBytes,
        });
        cost.TotalHigh.ShouldBeGreaterThanOrEqualTo(cost.TotalStandard);
    }

    [Test]
    public void Archive_MissingHighPriorityRates_FallBackToStandard_SoHighIsNotBelowStandard()
    {
        // A region whose Archive block has standard rates but omits the *High* fields (e.g. switzerlandwest,
        // for which Azure publishes no Priority archive meter). The high-priority rate must fall back to the
        // standard rate so High is never estimated cheaper than Standard.
        var pricing = new RegionPricing
        {
            EgressPerGb = 0.08,
            Archive     = new TierRates { DataRetrievalPerGb = 1.0, ReadOpsPer10k = 2.0 }, // no *High* fields
            Hot         = new TierRates { WriteOpsPer10k = 0.1, StoragePerGbMonth = 0.5 },
        };
        var cost = AzureRestoreCostCalculator.Compute(pricing, new RestoreCostRequest
        {
            ChunksNeedingRehydration = 1, BytesNeedingRehydration = OneGBBytes,
        });
        cost.RetrievalCostHigh.ShouldBe(cost.RetrievalCostStandard, tolerance: 1e-9);
        cost.ReadOpsCostHigh.ShouldBe(cost.ReadOpsCostStandard, tolerance: 1e-9);
        cost.TotalHigh.ShouldBe(cost.TotalStandard, tolerance: 1e-9);
    }

    [Test]
    public void EmbeddedCatalog_SwitzerlandWest_HighNotBelowStandard()
    {
        // switzerlandwest's archive omits the high-priority fields in pricing.json; via the rate fallback the
        // restore estimate must still keep TotalHigh >= TotalStandard rather than dropping the archive charges.
        var pricing = AzurePricingCatalog.LoadEmbedded().Resolve("switzerlandwest").Pricing;
        var cost = AzureRestoreCostCalculator.Compute(pricing, new RestoreCostRequest
        {
            ChunksNeedingRehydration = 1, BytesNeedingRehydration = OneGBBytes,
        });
        cost.RetrievalCostStandard.ShouldBeGreaterThan(0);
        cost.TotalHigh.ShouldBeGreaterThanOrEqualTo(cost.TotalStandard);
    }

    // ── Online download (read ops per tier + Cool/Cold retrieval) ────────────────

    [Test]
    public void DownloadCost_ReadOpsPerTier_PlusCoolColdRetrieval()
    {
        var cost = AzureRestoreCostCalculator.Compute(_pricing, new RestoreCostRequest
        {
            DownloadBytes = 3 * OneGBBytes,
            HotDownloadChunks = 10_000,  HotDownloadBytes  = OneGBBytes,
            CoolDownloadChunks = 10_000, CoolDownloadBytes = OneGBBytes,
            ColdDownloadChunks = 10_000, ColdDownloadBytes = OneGBBytes,
        });
        cost.DownloadReadOpsCost.ShouldBe(0.16, tolerance: 1e-9);   // 0.04 + 0.05 + 0.07
        cost.DownloadRetrievalCost.ShouldBe(1.5, tolerance: 1e-9);  // 0.6 + 0.9 (hot none)
        cost.RetrievalCostStandard.ShouldBe(0.0);
        cost.TotalStandard.ShouldBe(1.66, tolerance: 1e-9);
        cost.TotalHigh.ShouldBe(1.66, tolerance: 1e-9);
    }

    [Test]
    public void DownloadCost_HotTier_HasNoRetrievalCharge()
    {
        var cost = AzureRestoreCostCalculator.Compute(_pricing, new RestoreCostRequest
        {
            DownloadBytes = OneGBBytes, HotDownloadChunks = 10_000, HotDownloadBytes = OneGBBytes,
        });
        cost.DownloadRetrievalCost.ShouldBe(0.0);
        cost.DownloadReadOpsCost.ShouldBe(0.04, tolerance: 1e-9);
    }

    // ── Internet egress (first 100 GiB/month free) ──────────────────────────────

    [Test]
    public void EgressCost_BeyondFreeAllowance_ChargedPerGiB()
    {
        var cost = AzureRestoreCostCalculator.Compute(_pricing, new RestoreCostRequest { DownloadBytes = 200L * 1024 * 1024 * 1024 });
        cost.EgressCost.ShouldBe(8.0, tolerance: 1e-9); // (200-100) * 0.08
        cost.TotalStandard.ShouldBe(8.0, tolerance: 1e-9);
    }

    [Test]
    public void EgressCost_WithinFreeAllowance_IsZero()
        => AzureRestoreCostCalculator.Compute(_pricing, new RestoreCostRequest { DownloadBytes = 50L * 1024 * 1024 * 1024 })
            .EgressCost.ShouldBe(0.0);

    [Test]
    public void EgressCost_CountsRehydratedArchiveBytes_NotPending()
    {
        var cost = AzureRestoreCostCalculator.Compute(_pricing, new RestoreCostRequest
        {
            ChunksNeedingRehydration = 1, BytesNeedingRehydration = 60L * 1024 * 1024 * 1024,
            ChunksPendingRehydration = 1, BytesPendingRehydration = 500L * 1024 * 1024 * 1024,
            DownloadBytes = 80L * 1024 * 1024 * 1024,
        });
        cost.EgressCost.ShouldBe(3.2, tolerance: 1e-9); // (80+60-100) * 0.08
    }

    // ── Embedded catalog (real West Europe rates) ───────────────────────────────

    [Test]
    public void EmbeddedDefault_ArchiveRehydration_CostsArePositive_AndHighExceedsStandard()
    {
        var pricing = AzurePricingCatalog.LoadEmbedded().Resolve(null).Pricing; // westeurope
        var cost = AzureRestoreCostCalculator.Compute(pricing, new RestoreCostRequest
        {
            ChunksNeedingRehydration = 1, BytesNeedingRehydration = OneGBBytes,
        });
        cost.RetrievalCostStandard.ShouldBeGreaterThan(0);
        cost.ReadOpsCostStandard.ShouldBeGreaterThan(0);
        cost.WriteOpsCost.ShouldBeGreaterThan(0);
        cost.StorageCost.ShouldBeGreaterThan(0);
        cost.TotalHigh.ShouldBeGreaterThan(cost.TotalStandard);
    }
}
