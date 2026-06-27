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

/// <summary>
/// Per-region Azure Blob Storage rates loaded from <c>pricing.json</c>. A tier is <c>null</c> when the
/// region does not offer it (e.g. Belgium Central has no Archive tier for standard block blobs).
/// </summary>
[SharedWithinAssembly] // consumed by RestoreCostCalculator (Features.RestoreCommand) + StorageCostCalculator
internal sealed record RegionPricing
{
    /// <summary>ISO currency code the rates are expressed in (e.g. <c>EUR</c>).</summary>
    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "EUR";

    /// <summary>Internet egress (data transfer out) per GiB beyond the monthly free allowance; 0 if not configured.</summary>
    [JsonPropertyName("egressPerGB")]
    public double EgressPerGb { get; init; }

    [JsonPropertyName("hot")]     public TierRates? Hot     { get; init; }
    [JsonPropertyName("cool")]    public TierRates? Cool    { get; init; }
    [JsonPropertyName("cold")]    public TierRates? Cold    { get; init; }
    [JsonPropertyName("archive")] public TierRates? Archive { get; init; }

    /// <summary>The rate set for a tier, or <c>null</c> if the region doesn't offer it.</summary>
    public TierRates? For(BlobTier tier) => tier switch
    {
        BlobTier.Hot     => Hot,
        BlobTier.Cool    => Cool,
        BlobTier.Cold    => Cold,
        BlobTier.Archive => Archive,
        _                => null,
    };

    /// <summary>Storage rate (currency per GiB per month) for a tier; 0 when the tier is unavailable.</summary>
    public double StorageRateFor(BlobTier tier) => For(tier)?.StoragePerGbMonth ?? 0.0;

    /// <summary>Write-operations rate (currency per 10,000) for a tier; 0 when unavailable.</summary>
    public double WriteOpsRateFor(BlobTier tier) => For(tier)?.WriteOpsPer10k ?? 0.0;

    /// <summary>Read-operations rate (currency per 10,000) for a tier; the high-priority rate applies only to Archive. 0 when unavailable.</summary>
    public double ReadOpsRateFor(BlobTier tier, bool highPriority = false)
        => For(tier) is { } r ? (highPriority ? r.ReadOpsHighPer10k : r.ReadOpsPer10k) : 0.0;

    /// <summary>Data-retrieval rate (currency per GiB) charged when reading from a tier; 0 for Hot and unavailable tiers. High priority applies only to Archive.</summary>
    public double DataRetrievalRateFor(BlobTier tier, bool highPriority = false)
        => For(tier) is { } r ? (highPriority ? r.DataRetrievalHighPerGb : r.DataRetrievalPerGb) : 0.0;
}

/// <summary>
/// Rates for one access tier. Operation rates are per 10,000 operations; per-GB values are per binary GiB.
/// <see cref="DataRetrievalPerGb"/> is 0 for Hot; the <c>*High*</c> fields are populated only for Archive.
/// </summary>
[SharedWithinAssembly] // accessed by RestoreCostCalculator (Features.RestoreCommand)
internal sealed record TierRates
{
    /// <summary>Currency per GiB stored per month.</summary>
    [JsonPropertyName("storagePerGBPerMonth")]
    public double StoragePerGbMonth { get; init; }

    /// <summary>Currency per 10,000 write operations.</summary>
    [JsonPropertyName("writeOpsPer10000")]
    public double WriteOpsPer10k { get; init; }

    /// <summary>Currency per 10,000 read operations (Standard priority for Archive).</summary>
    [JsonPropertyName("readOpsPer10000")]
    public double ReadOpsPer10k { get; init; }

    /// <summary>Currency per 10,000 read operations at High priority (Archive only).</summary>
    [JsonPropertyName("readOpsHighPer10000")]
    public double ReadOpsHighPer10k { get; init; }

    /// <summary>Currency per GiB of data retrieved when reading (Cool/Cold/Archive; Standard priority for Archive). 0 for Hot.</summary>
    [JsonPropertyName("dataRetrievalPerGB")]
    public double DataRetrievalPerGb { get; init; }

    /// <summary>Currency per GiB of data retrieved at High priority (Archive only).</summary>
    [JsonPropertyName("dataRetrievalHighPerGB")]
    public double DataRetrievalHighPerGb { get; init; }
}
