using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arius.Core.Features.RestoreCommand;

/// <summary>
/// Pricing rates for archive-tier read operations.
/// </summary>
internal sealed class ArchivePricingTier
{
    /// <summary>EUR per GB of data retrieved from Archive at Standard priority.</summary>
    [JsonPropertyName("retrievalPerGB")]
    public double RetrievalPerGB { get; init; }

    /// <summary>EUR per GB of data retrieved from Archive at High priority.</summary>
    [JsonPropertyName("retrievalHighPerGB")]
    public double RetrievalHighPerGB { get; init; }

    /// <summary>EUR per 10,000 read operations at Standard priority.</summary>
    [JsonPropertyName("readOpsPer10000")]
    public double ReadOpsPer10000 { get; init; }

    /// <summary>EUR per 10,000 read operations at High priority.</summary>
    [JsonPropertyName("readOpsHighPer10000")]
    public double ReadOpsHighPer10000 { get; init; }
}

/// <summary>
/// Pricing rates for a target storage tier (Hot/Cool/Cold).
/// </summary>
internal sealed class TierPricingConfig
{
    /// <summary>EUR per 10,000 write operations.</summary>
    [JsonPropertyName("writeOpsPer10000")]
    public double WriteOpsPer10000 { get; init; }

    /// <summary>EUR per GB stored per month.</summary>
    [JsonPropertyName("storagePerGBPerMonth")]
    public double StoragePerGBPerMonth { get; init; }
}

/// <summary>
/// Root pricing configuration loaded from <c>pricing.json</c>.
/// </summary>
internal sealed class PricingConfig
{
    [JsonPropertyName("archive")]
    public ArchivePricingTier Archive { get; init; } = new();

    [JsonPropertyName("hot")]
    public TierPricingConfig Hot { get; init; } = new();

    [JsonPropertyName("cool")]
    public TierPricingConfig Cool { get; init; } = new();

    [JsonPropertyName("cold")]
    public TierPricingConfig Cold { get; init; } = new();

    // ── Loading ───────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive  = true,
        ReadCommentHandling          = JsonCommentHandling.Skip,
        AllowTrailingCommas          = true,
    };

    /// <summary>
    /// Loads the pricing configuration.
    ///
    /// Override search order (first found wins):
    /// <list type="number">
    ///   <item><description>Current working directory: <c>pricing.json</c></description></item>
    ///   <item><description>User config directory: <c>~/.arius/pricing.json</c></description></item>
    ///   <item><description>Embedded resource default (EUR West Europe rates).</description></item>
    /// </list>
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when a found override file cannot be parsed.</exception>
    public static PricingConfig Load()
    {
        var currentDirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), "pricing.json");

        // 1. Working directory override
        if (File.Exists(currentDirectoryPath))
            return LoadFromFile(currentDirectoryPath);

        // 2. ~/.arius/ override
        var ariusConfigPath = Path.Combine(GetUserHomeDirectory(), ".arius", "pricing.json");
        if (File.Exists(ariusConfigPath))
            return LoadFromFile(ariusConfigPath);

        // 3. Embedded resource default
        return LoadEmbedded();
    }

    public static PricingConfig LoadFromFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return LoadFromFile(stream, path);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse pricing config at '{path}': {ex.Message}", ex);
        }
    }

    private static PricingConfig LoadFromFile(Stream stream, string sourceDescription)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            var result = JsonSerializer.Deserialize<PricingConfig>(stream, _jsonOptions);
            return result ?? throw new InvalidOperationException($"Pricing file deserialized to null: {sourceDescription}");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse pricing config at '{sourceDescription}': {ex.Message}", ex);
        }
    }

    private static string GetUserHomeDirectory()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
            return home;

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public static PricingConfig LoadEmbedded()
    {
        var assembly     = typeof(PricingConfig).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("pricing.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded pricing.json resource not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        var result = JsonSerializer.Deserialize<PricingConfig>(stream, _jsonOptions);
        return result ?? throw new InvalidOperationException("Embedded pricing.json deserialized to null.");
    }
}
