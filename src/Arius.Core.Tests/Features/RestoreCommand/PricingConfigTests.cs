using System.Globalization;
using Arius.Core.Features.RestoreCommand;

namespace Arius.Core.Tests.Features.RestoreCommand;

[NotInParallel("PricingConfigGlobalState")]
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

    [Test]
    public void Load_HomeAriusOverride_IsUsedWhenCurrentDirectoryOverrideMissing()
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"pricing-home-{Guid.NewGuid():N}");
        var cwdRoot = Path.Combine(tempRoot, "cwd");
        var homeRoot = Path.Combine(tempRoot, "home");

        Directory.CreateDirectory(cwdRoot);
        Directory.CreateDirectory(Path.Combine(homeRoot, ".arius"));

        try
        {
            File.WriteAllText(Path.Combine(homeRoot, ".arius", "pricing.json"), OverrideJson(3.0, 4.0, 5.0, 6.0, 0.3, 0.04));
            Directory.SetCurrentDirectory(cwdRoot);
            Environment.SetEnvironmentVariable("HOME", homeRoot);

            var config = PricingConfig.Load();

            config.Archive.RetrievalPerGB.ShouldBe(3.0);
            config.Archive.RetrievalHighPerGB.ShouldBe(4.0);
            config.Archive.ReadOpsPer10000.ShouldBe(5.0);
            config.Archive.ReadOpsHighPer10000.ShouldBe(6.0);
            config.Hot.WriteOpsPer10000.ShouldBe(0.3);
            config.Hot.StoragePerGBPerMonth.ShouldBe(0.04);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Environment.SetEnvironmentVariable("HOME", originalHome);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Test]
    public void Load_CurrentDirectoryOverride_WinsOverHomeAriusOverride()
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"pricing-precedence-{Guid.NewGuid():N}");
        var cwdRoot = Path.Combine(tempRoot, "cwd");
        var homeRoot = Path.Combine(tempRoot, "home");

        Directory.CreateDirectory(cwdRoot);
        Directory.CreateDirectory(Path.Combine(homeRoot, ".arius"));

        try
        {
            File.WriteAllText(Path.Combine(cwdRoot, "pricing.json"), OverrideJson(7.0, 8.0, 9.0, 10.0, 0.7, 0.08));
            File.WriteAllText(Path.Combine(homeRoot, ".arius", "pricing.json"), OverrideJson(3.0, 4.0, 5.0, 6.0, 0.3, 0.04));
            Directory.SetCurrentDirectory(cwdRoot);
            Environment.SetEnvironmentVariable("HOME", homeRoot);

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
            Environment.SetEnvironmentVariable("HOME", originalHome);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string OverrideJson(
        double retrievalPerGb,
        double retrievalHighPerGb,
        double readOpsPer10000,
        double readOpsHighPer10000,
        double hotWriteOpsPer10000,
        double hotStoragePerGbPerMonth)
    {
        static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);

        return
        $@"{{
    ""archive"": {{
        ""retrievalPerGB"": {Format(retrievalPerGb)},
        ""retrievalHighPerGB"": {Format(retrievalHighPerGb)},
        ""readOpsPer10000"": {Format(readOpsPer10000)},
        ""readOpsHighPer10000"": {Format(readOpsHighPer10000)}
    }},
    ""hot"": {{ ""writeOpsPer10000"": {Format(hotWriteOpsPer10000)}, ""storagePerGBPerMonth"": {Format(hotStoragePerGbPerMonth)} }},
    ""cool"": {{ ""writeOpsPer10000"": 0.2, ""storagePerGBPerMonth"": 0.01 }},
    ""cold"": {{ ""writeOpsPer10000"": 0.3, ""storagePerGBPerMonth"": 0.005 }}
}}";
    }
}
