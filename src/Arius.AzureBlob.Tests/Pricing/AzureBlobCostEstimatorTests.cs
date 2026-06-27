using Arius.AzureBlob.Pricing;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;

namespace Arius.AzureBlob.Tests.Pricing;

public class AzureBlobCostEstimatorTests
{
    private const double GiB = 1024.0 * 1024.0 * 1024.0;
    private static readonly AzureBlobCostEstimator _estimator = new();
    private static readonly AzurePricingCatalog _catalog = AzurePricingCatalog.LoadEmbedded();

    // ── Regions ─────────────────────────────────────────────────────────────────

    [Test]
    public void Regions_CoverStandardPublicRegions_AndExcludeNonCommercialClouds()
    {
        var regions = _estimator.Regions;
        regions.ShouldContain("westeurope");
        regions.ShouldContain("northeurope");
        regions.ShouldContain("belgiumcentral");
        regions.ShouldNotContain("usgovvirginia"); // Government cloud excluded
        regions.ShouldNotContain("attatlanta1");   // MEC excluded
        regions.Count.ShouldBeGreaterThan(40);
    }

    // ── Storage cost ──────────────────────────────────────────────────────────────

    [Test]
    public void EstimateStorageCost_PricesEachTierBySizeTimesRegionRate()
    {
        var byTier = new List<ChunkTierStatistic>
        {
            new(BlobTier.Cool, UniqueChunks: 2, StoredSize: 40),
            new(BlobTier.Archive, UniqueChunks: 1, StoredSize: 60),
        };
        var pricing = _catalog.Resolve(null).Pricing; // westeurope

        var estimate = _estimator.EstimateStorageCost(null, byTier);

        estimate.Region.ShouldBe("westeurope");
        estimate.Tiers[0].CostPerMonth.ShouldBe(40 / GiB * pricing.StorageRateFor(BlobTier.Cool), tolerance: 1e-18);
        estimate.Tiers[1].CostPerMonth.ShouldBe(60 / GiB * pricing.StorageRateFor(BlobTier.Archive), tolerance: 1e-18);
        estimate.TotalPerMonth.ShouldBe(estimate.Tiers.Sum(t => t.CostPerMonth), tolerance: 1e-18);
    }

    [Test]
    public void EstimateStorageCost_UnknownRegion_FallsBackToDefault()
    {
        var estimate = _estimator.EstimateStorageCost("not-a-region", []);
        estimate.Region.ShouldBe("westeurope");
    }

    [Test]
    public void EstimateStorageCost_RegionWithoutArchive_PricesArchiveAtZero()
    {
        // Belgium Central offers no Archive tier — its archive storage rate is therefore 0.
        var estimate = _estimator.EstimateStorageCost("belgiumcentral",
            [new ChunkTierStatistic(BlobTier.Archive, UniqueChunks: 1, StoredSize: 1_000_000_000)]);
        estimate.Region.ShouldBe("belgiumcentral");
        estimate.Tiers[0].CostPerMonth.ShouldBe(0.0);
    }

    [Test]
    public void EstimateStorageCost_RegionRatesDiffer()
    {
        var byTier = new List<ChunkTierStatistic> { new(BlobTier.Hot, 1, 1_000_000_000_000) };
        var we = _estimator.EstimateStorageCost("westeurope", byTier).TotalPerMonth;
        var au = _estimator.EstimateStorageCost("australiaeast", byTier).TotalPerMonth;
        au.ShouldNotBe(we); // per-region Hot storage rates differ
    }

    // ── Restore cost (slim canonical estimate) ──────────────────────────────────

    [Test]
    public void EstimateRestoreCost_ReturnsCountsAndPositiveTotals_ForArchive()
    {
        var estimate = _estimator.EstimateRestoreCost(null, new RestoreCostRequest
        {
            ChunksNeedingRehydration = 3,
            BytesNeedingRehydration  = 5L * 1024 * 1024 * 1024,
            DownloadBytes            = 5L * 1024 * 1024 * 1024,
        });
        estimate.ChunksNeedingRehydration.ShouldBe(3);
        estimate.TotalStandard.ShouldBeGreaterThan(0);
        estimate.TotalHigh.ShouldBeGreaterThan(estimate.TotalStandard);
    }
}
