using Arius.Core.Features.RestoreCommand;

namespace Arius.Core.Tests.Features.RestoreCommand;

public class PricingConfigTests
{
    [Test]
    public void LoadEmbedded_ReturnsNonNullConfig()
    {
        var config = PricingConfig.LoadEmbedded();

        config.ShouldNotBeNull();
    }

    [Test]
    public void LoadEmbedded_ArchiveTier_HasPositiveRates()
    {
        var config = PricingConfig.LoadEmbedded();

        config.Archive.RetrievalPerGB.ShouldBeGreaterThan(0);
        config.Archive.RetrievalHighPerGB.ShouldBeGreaterThan(0);
        config.Archive.ReadOpsPer10000.ShouldBeGreaterThan(0);
        config.Archive.ReadOpsHighPer10000.ShouldBeGreaterThan(0);
    }

    [Test]
    public void LoadEmbedded_HotTier_HasPositiveRates()
    {
        var config = PricingConfig.LoadEmbedded();

        config.Hot.WriteOpsPer10000.ShouldBeGreaterThan(0);
        config.Hot.StoragePerGBPerMonth.ShouldBeGreaterThan(0);
    }

    [Test]
    public void LoadEmbedded_HighPriorityRetrievalMoreExpensiveThanStandard()
    {
        var config = PricingConfig.LoadEmbedded();

        config.Archive.RetrievalHighPerGB.ShouldBeGreaterThan(config.Archive.RetrievalPerGB);
        config.Archive.ReadOpsHighPer10000.ShouldBeGreaterThan(config.Archive.ReadOpsPer10000);
    }

    [Test]
    public void LoadFromFile_ValidOverride_ReturnsParsedRates()
    {
        var json = """
        {
            "archive": {
                "retrievalPerGB": 1.0,
                "retrievalHighPerGB": 5.0,
                "readOpsPer10000": 2.0,
                "readOpsHighPer10000": 10.0
            },
            "hot": { "writeOpsPer10000": 0.1, "storagePerGBPerMonth": 0.02 },
            "cool": { "writeOpsPer10000": 0.2, "storagePerGBPerMonth": 0.01 },
            "cold": { "writeOpsPer10000": 0.3, "storagePerGBPerMonth": 0.005 }
        }
        """;

        var path = Path.Combine(Path.GetTempPath(), $"pricing_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, json);

            var config = PricingConfig.LoadFromFile(path);

            config.Archive.RetrievalPerGB.ShouldBe(1.0);
            config.Archive.RetrievalHighPerGB.ShouldBe(5.0);
            config.Hot.WriteOpsPer10000.ShouldBe(0.1);
            config.Hot.StoragePerGBPerMonth.ShouldBe(0.02);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void LoadFromFile_MalformedJson_ThrowsInvalidOperationException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pricing_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not valid json !!!");
            Should.Throw<InvalidOperationException>(() => PricingConfig.LoadFromFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Load_CurrentDirectoryOverride_ReturnsParsedRates()
    {
        var json = """
        {
            "archive": {
                "retrievalPerGB": 7.0,
                "retrievalHighPerGB": 8.0,
                "readOpsPer10000": 9.0,
                "readOpsHighPer10000": 10.0
            },
            "hot": { "writeOpsPer10000": 0.7, "storagePerGBPerMonth": 0.08 },
            "cool": { "writeOpsPer10000": 0.2, "storagePerGBPerMonth": 0.01 },
            "cold": { "writeOpsPer10000": 0.3, "storagePerGBPerMonth": 0.005 }
        }
        """;

        var originalDirectory = Directory.GetCurrentDirectory();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"pricing-load-{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempRoot);

        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "pricing.json"), json);
            Directory.SetCurrentDirectory(tempRoot);

            var config = PricingConfig.Load();

            config.Archive.RetrievalPerGB.ShouldBe(7.0);
            config.Archive.RetrievalHighPerGB.ShouldBe(8.0);
            config.Archive.ReadOpsPer10000.ShouldBe(9.0);
            config.Archive.ReadOpsHighPer10000.ShouldBe(10.0);
            config.Hot.WriteOpsPer10000.ShouldBe(0.7);
            config.Hot.StoragePerGBPerMonth.ShouldBe(0.08);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
