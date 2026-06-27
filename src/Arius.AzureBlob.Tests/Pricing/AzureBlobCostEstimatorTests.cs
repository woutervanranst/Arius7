using Arius.AzureBlob;
using Arius.AzureBlob.Pricing;
using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arius.AzureBlob.Tests.Pricing;

public class AzureBlobCostEstimatorTests
{
    private const double GiB = 1024.0 * 1024.0 * 1024.0;
    private static readonly AzurePricingCatalog _catalog = AzurePricingCatalog.LoadEmbedded();

    /// <summary>Builds an estimator bound to <paramref name="regionHint"/> (null = container metadata not set).</summary>
    private static AzureBlobCostEstimator For(string? regionHint)
        => new(_catalog, regionHint, NullLogger<AzureBlobCostEstimator>.Instance);

    // ── Catalog regions ─────────────────────────────────────────────────────────

    [Test]
    public void Catalog_CoversStandardPublicRegions_AndExcludesNonCommercialClouds()
    {
        var regions = _catalog.RegionNames;
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
        var pricing = _catalog.Resolve("westeurope").Pricing;

        var estimate = For("westeurope").EstimateStorageCost(byTier);

        estimate.Region.ShouldBe("westeurope");
        estimate.Tiers[0].CostPerMonth.ShouldBe(40 / GiB * pricing.StorageRateFor(BlobTier.Cool), tolerance: 1e-18);
        estimate.Tiers[1].CostPerMonth.ShouldBe(60 / GiB * pricing.StorageRateFor(BlobTier.Archive), tolerance: 1e-18);
        estimate.TotalPerMonth.ShouldBe(estimate.Tiers.Sum(t => t.CostPerMonth), tolerance: 1e-18);
    }

    [Test]
    public void EstimateStorageCost_UnsetRegion_FallsBackToDefaultRegion()
    {
        // A container with no 'region' metadata (null hint) prices against the default region with a warning.
        var estimate = For(null).EstimateStorageCost([]);
        estimate.Region.ShouldBe(AzureBlobContainerService.DefaultRegion);
    }

    [Test]
    public void EstimateStorageCost_UnknownRegion_FallsBackToCatalogDefault()
    {
        // A non-empty but unpriced region falls back to the catalog's own default.
        var estimate = For("not-a-region").EstimateStorageCost([]);
        estimate.Region.ShouldBe("westeurope");
    }

    [Test]
    public void EstimateStorageCost_RegionWithoutArchive_PricesArchiveAtZero()
    {
        // Belgium Central offers no Archive tier — its archive storage rate is therefore 0.
        var estimate = For("belgiumcentral").EstimateStorageCost(
            [new ChunkTierStatistic(BlobTier.Archive, UniqueChunks: 1, StoredSize: 1_000_000_000)]);
        estimate.Region.ShouldBe("belgiumcentral");
        estimate.Tiers[0].CostPerMonth.ShouldBe(0.0);
    }

    [Test]
    public void EstimateStorageCost_RegionRatesDiffer()
    {
        var byTier = new List<ChunkTierStatistic> { new(BlobTier.Hot, 1, 1_000_000_000_000) };
        var we = For("westeurope").EstimateStorageCost(byTier).TotalPerMonth;
        var au = For("australiaeast").EstimateStorageCost(byTier).TotalPerMonth;
        au.ShouldNotBe(we); // per-region Hot storage rates differ
    }

    // ── Restore cost (slim canonical estimate) ──────────────────────────────────

    [Test]
    public void EstimateRestoreCost_ReturnsCountsAndPositiveTotals_ForArchive()
    {
        var estimate = For("westeurope").EstimateRestoreCost(new RestoreCostRequest
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
