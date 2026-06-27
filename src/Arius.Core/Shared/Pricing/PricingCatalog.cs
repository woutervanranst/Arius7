using System.Text.Json;
using System.Text.Json.Serialization;
using Arius.Core.Shared.Storage;

namespace Arius.Core.Shared.Pricing;

/// <summary>
/// The canonical, region-keyed Azure Blob Storage pricing — the single source of truth loaded from the
/// embedded <c>pricing.json</c>. Both the restore-cost path (<c>RestoreCostCalculator</c>) and the
/// storage-cost path (<c>StorageCostCalculator</c>) resolve their rates through here, and the set of
/// selectable regions in the UI is derived from <see cref="RegionNames"/>.
/// </summary>
public sealed class PricingCatalog
{
    /// <summary>The region used when a requested region is null, "Unknown", or absent from the catalog.</summary>
    private const string DefaultRegionName = "westeurope";

    private readonly IReadOnlyDictionary<string, RegionPricing> _regions;
    private readonly string _defaultRegion;

    private PricingCatalog(IReadOnlyDictionary<string, RegionPricing> regions)
    {
        _regions = regions;
        _defaultRegion = regions.ContainsKey(DefaultRegionName)
            ? DefaultRegionName
            : regions.Keys.FirstOrDefault() ?? throw new InvalidOperationException("pricing.json contains no regions.");
    }

    /// <summary>The programmatic region names present in the catalog (e.g. <c>westeurope</c>), for the account-region dropdown.</summary>
    public IReadOnlyList<string> RegionNames => _regions.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Resolves a requested region to its pricing and the actual region name applied. A null/"Unknown"/unknown
    /// region falls back to the default region (so cost is always estimable).
    /// </summary>
    internal (string Name, RegionPricing Pricing) Resolve(string? region)
    {
        if (!string.IsNullOrWhiteSpace(region) && _regions.TryGetValue(region, out var pricing))
            return (region, pricing);
        return (_defaultRegion, _regions[_defaultRegion]);
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PricingCatalog LoadEmbedded()
    {
        var assembly = typeof(PricingCatalog).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
                               .FirstOrDefault(n => n.EndsWith("pricing.json", StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidOperationException("Embedded pricing.json resource not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        var document = JsonSerializer.Deserialize<PricingDocument>(stream, _jsonOptions)
                       ?? throw new InvalidOperationException("Embedded pricing.json deserialized to null.");
        if (document.Regions.Count == 0)
            throw new InvalidOperationException("Embedded pricing.json contains no regions.");
        return new PricingCatalog(document.Regions);
    }

    private sealed record PricingDocument
    {
        [JsonPropertyName("regions")]
        public Dictionary<string, RegionPricing> Regions { get; init; } = new();
    }
}

/// <summary>Per-region Azure Blob Storage rates loaded from <c>pricing.json</c>.</summary>
[SharedWithinAssembly] // consumed by RestoreCostCalculator (Features.RestoreCommand) + StorageCostCalculator
internal sealed record RegionPricing
{
    /// <summary>ISO currency code the rates are expressed in (e.g. <c>EUR</c>).</summary>
    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "EUR";

    [JsonPropertyName("archive")]
    public ArchivePricingTier Archive { get; init; } = new();

    [JsonPropertyName("hot")]
    public TierPricingConfig Hot { get; init; } = new();

    [JsonPropertyName("cool")]
    public TierPricingConfig Cool { get; init; } = new();

    [JsonPropertyName("cold")]
    public TierPricingConfig Cold { get; init; } = new();

    /// <summary>Storage rate (currency per GiB per month) for a given blob tier; 0 for unknown tiers.</summary>
    public double StorageRateFor(BlobTier tier) => tier switch
    {
        BlobTier.Hot     => Hot.StoragePerGBPerMonth,
        BlobTier.Cool    => Cool.StoragePerGBPerMonth,
        BlobTier.Cold    => Cold.StoragePerGBPerMonth,
        BlobTier.Archive => Archive.StoragePerGBPerMonth,
        _                => 0.0,
    };
}

/// <summary>Pricing rates for the archive tier (retrieval + read operations, plus storage for the cost view).</summary>
[SharedWithinAssembly] // accessed by RestoreCostCalculator (Features.RestoreCommand)
internal sealed record ArchivePricingTier
{
    /// <summary>Currency per GB of data retrieved from Archive at Standard priority.</summary>
    [JsonPropertyName("retrievalPerGB")]
    public double RetrievalPerGB { get; init; }

    /// <summary>Currency per GB of data retrieved from Archive at High priority.</summary>
    [JsonPropertyName("retrievalHighPerGB")]
    public double RetrievalHighPerGB { get; init; }

    /// <summary>Currency per 10,000 read operations at Standard priority.</summary>
    [JsonPropertyName("readOpsPer10000")]
    public double ReadOpsPer10000 { get; init; }

    /// <summary>Currency per 10,000 read operations at High priority.</summary>
    [JsonPropertyName("readOpsHighPer10000")]
    public double ReadOpsHighPer10000 { get; init; }

    /// <summary>Currency per GB stored per month in the Archive tier.</summary>
    [JsonPropertyName("storagePerGBPerMonth")]
    public double StoragePerGBPerMonth { get; init; }
}

/// <summary>Pricing rates for writing and storing in a (non-archive) tier.</summary>
[SharedWithinAssembly] // accessed by RestoreCostCalculator (Features.RestoreCommand)
internal sealed record TierPricingConfig
{
    /// <summary>Currency per 10,000 write operations.</summary>
    [JsonPropertyName("writeOpsPer10000")]
    public double WriteOpsPer10000 { get; init; }

    /// <summary>Currency per GB stored per month.</summary>
    [JsonPropertyName("storagePerGBPerMonth")]
    public double StoragePerGBPerMonth { get; init; }
}
