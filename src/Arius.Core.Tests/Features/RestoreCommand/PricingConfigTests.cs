using System.Text.Json;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Pricing;

namespace Arius.Core.Tests.Features.RestoreCommand;

public class PricingConfigTests
{
    [Test]
    public void JsonRoundTrip_PreservesConfiguredRates()
    {
        var config = CreatePricingConfig(
            retrievalPerGb: 1.0,
            retrievalHighPerGb: 5.0,
            readOpsPer10000: 2.0,
            readOpsHighPer10000: 10.0,
            hotWriteOpsPer10000: 0.1,
            hotStoragePerGbPerMonth: 0.02);

        config.Archive.RetrievalPerGB.ShouldBe(1.0);
        config.Archive.RetrievalHighPerGB.ShouldBe(5.0);
        config.Archive.ReadOpsPer10000.ShouldBe(2.0);
        config.Archive.ReadOpsHighPer10000.ShouldBe(10.0);
        config.Hot.WriteOpsPer10000.ShouldBe(0.1);
        config.Hot.StoragePerGBPerMonth.ShouldBe(0.02);
    }

    [Test]
    public void JsonRoundTrip_MalformedJson_ThrowsJsonException()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<RegionPricing>("{ not valid json !!!"));
    }

    [Test]
    public void RestoreCostCalculator_WhenPricingProvided_UsesSuppliedPricing()
    {
        var suppliedPricing = CreatePricingConfig(3.0, 4.0, 5.0, 6.0, 0.3, 0.04);
        var estimate = new RestoreCostCalculator(suppliedPricing).Compute(
            chunksAvailable: 0,
            chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1,
            chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes,
            bytesPendingRehydration: 0,
            downloadBytes: 0);

        estimate.RetrievalCostStandard.ShouldBe(3.0);
        estimate.RetrievalCostHigh.ShouldBe(4.0);
        estimate.ReadOpsCostStandard.ShouldBe(0.0005, tolerance: 1e-9);
        estimate.ReadOpsCostHigh.ShouldBe(0.0006, tolerance: 1e-9);
        estimate.WriteOpsCost.ShouldBe(0.00003, tolerance: 1e-9);
        estimate.StorageCost.ShouldBe(0.04, tolerance: 1e-9);
    }

    [Test]
    public void RestoreCostCalculator_WhenPricingProvided_WinsOverEmbeddedDefault()
    {
        var suppliedPricing = CreatePricingConfig(7.0, 8.0, 9.0, 10.0, 0.7, 0.08);
        var embeddedPricing = new RestoreCostCalculator(pricing: null).Compute(
            chunksAvailable: 0,
            chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1,
            chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes,
            bytesPendingRehydration: 0,
            downloadBytes: 0);
        var estimate = new RestoreCostCalculator(suppliedPricing).Compute(
            chunksAvailable: 0,
            chunksAlreadyRehydrated: 0,
            chunksNeedingRehydration: 1,
            chunksPendingRehydration: 0,
            bytesNeedingRehydration: OneGBBytes,
            bytesPendingRehydration: 0,
            downloadBytes: 0);

        estimate.RetrievalCostStandard.ShouldBe(7.0);
        estimate.RetrievalCostStandard.ShouldNotBe(embeddedPricing.RetrievalCostStandard);
        estimate.RetrievalCostHigh.ShouldBe(8.0);
        estimate.ReadOpsCostStandard.ShouldBe(0.0009, tolerance: 1e-9);
        estimate.ReadOpsCostHigh.ShouldBe(0.001, tolerance: 1e-9);
        estimate.WriteOpsCost.ShouldBe(0.00007, tolerance: 1e-9);
        estimate.StorageCost.ShouldBe(0.08, tolerance: 1e-9);
    }

    private static readonly long OneGBBytes = 1L * 1024 * 1024 * 1024;

    private static RegionPricing CreatePricingConfig(
        double retrievalPerGb = 1.0,
        double retrievalHighPerGb = 5.0,
        double readOpsPer10000 = 2.0,
        double readOpsHighPer10000 = 10.0,
        double hotWriteOpsPer10000 = 0.1,
        double hotStoragePerGbPerMonth = 0.02)
    {
        return new RegionPricing
        {
            Archive = new ArchivePricingTier
            {
                RetrievalPerGB      = retrievalPerGb,
                RetrievalHighPerGB  = retrievalHighPerGb,
                ReadOpsPer10000     = readOpsPer10000,
                ReadOpsHighPer10000 = readOpsHighPer10000,
            },
            Hot = new TierPricingConfig
            {
                WriteOpsPer10000     = hotWriteOpsPer10000,
                StoragePerGBPerMonth = hotStoragePerGbPerMonth,
            },
            Cool = new TierPricingConfig
            {
                WriteOpsPer10000     = 0.2,
                StoragePerGBPerMonth = 0.01,
            },
            Cold = new TierPricingConfig
            {
                WriteOpsPer10000     = 0.3,
                StoragePerGBPerMonth = 0.005,
            },
        };
    }
}
