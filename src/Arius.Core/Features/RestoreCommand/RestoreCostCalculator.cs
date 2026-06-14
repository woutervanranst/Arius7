using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arius.Core.Features.RestoreCommand;

/// <summary>
/// Computes the restore cost estimate shown before downloads or rehydration begin.
/// </summary>
internal sealed class RestoreCostCalculator(PricingConfig? pricing)
{
    private readonly PricingConfig _pricing = pricing ?? PricingConfig.LoadEmbedded();

    /// <summary>
    /// Computes a <see cref="RestoreCostEstimate"/> from classified chunk counts and byte totals.
    /// </summary>
    /// <param name="chunksAvailable">Chunk count ready for immediate download.</param>
    /// <param name="chunksAlreadyRehydrated">Archive-tier chunk count with ready rehydrated copies.</param>
    /// <param name="chunksNeedingRehydration">Archive-tier chunk count that needs rehydration.</param>
    /// <param name="chunksPendingRehydration">Archive-tier chunk count with rehydration already pending.</param>
    /// <param name="rehydrationBytes">Compressed bytes that require rehydration or are already pending.</param>
    /// <param name="downloadBytes">Compressed bytes available for immediate download.</param>
    /// <param name="monthsStored">Storage duration assumed for rehydrated chunk copies.</param>
    public RestoreCostEstimate Compute(
        int            chunksAvailable,
        int            chunksAlreadyRehydrated,
        int            chunksNeedingRehydration,
        int            chunksPendingRehydration,
        long           rehydrationBytes,
        long           downloadBytes,
        double         monthsStored = 1.0)
    {
        var numberOfBlobs = chunksNeedingRehydration + chunksPendingRehydration;
        var totalGB       = rehydrationBytes / (1024.0 * 1024.0 * 1024.0);
        var opsUnits      = numberOfBlobs / 10_000.0;

        return new RestoreCostEstimate
        {
            ChunksAvailable          = chunksAvailable,
            ChunksAlreadyRehydrated  = chunksAlreadyRehydrated,
            ChunksNeedingRehydration = chunksNeedingRehydration,
            ChunksPendingRehydration = chunksPendingRehydration,
            RehydrationBytes         = rehydrationBytes,
            DownloadBytes            = downloadBytes,

            // Retrieval cost: per GB from archive
            RetrievalCostStandard = totalGB * _pricing.Archive.RetrievalPerGB,
            RetrievalCostHigh     = totalGB * _pricing.Archive.RetrievalHighPerGB,

            // Read ops: (N/10000) * rate — Azure charges per operation, not per batch
            ReadOpsCostStandard   = opsUnits * _pricing.Archive.ReadOpsPer10000,
            ReadOpsCostHigh       = opsUnits * _pricing.Archive.ReadOpsHighPer10000,

            // Write ops: (N/10000) * rate to Hot tier
            WriteOpsCost          = opsUnits * _pricing.Hot.WriteOpsPer10000,

            // Storage: N months in Hot tier (rehydrated copies in chunks-rehydrated/)
            StorageCost           = totalGB * _pricing.Hot.StoragePerGBPerMonth * monthsStored,
        };
    }
}

// --- PRICING CONFIG

/// <summary>
/// Restore pricing configuration loaded from <c>pricing.json</c>.
/// </summary>
internal sealed record PricingConfig
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
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PricingConfig LoadEmbedded()
    {
        var assembly = typeof(PricingConfig).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
                               .FirstOrDefault(n => n.EndsWith("pricing.json", StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidOperationException("Embedded pricing.json resource not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        var result = JsonSerializer.Deserialize<PricingConfig>(stream, _jsonOptions);
        return result ?? throw new InvalidOperationException("Embedded pricing.json deserialized to null.");
    }
}

/// <summary>
/// Pricing rates used for archive-tier retrieval and read operations.
/// </summary>
internal sealed record ArchivePricingTier
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
/// Pricing rates used for writing and storing rehydrated chunk copies in a target tier.
/// </summary>
internal sealed record TierPricingConfig
{
    /// <summary>EUR per 10,000 write operations.</summary>
    [JsonPropertyName("writeOpsPer10000")]
    public double WriteOpsPer10000 { get; init; }

    /// <summary>EUR per GB stored per month.</summary>
    [JsonPropertyName("storagePerGBPerMonth")]
    public double StoragePerGBPerMonth { get; init; }
}


// --- COST ESTIMATE

/// <summary>
/// Restore cost estimate presented before downloads or rehydration begin.
/// Monetary values use the currency configured in <c>pricing.json</c> (default: EUR).
/// </summary>
public sealed record RestoreCostEstimate
{
    // ── Chunk availability counts ─────────────────────────────────────────────

    /// <summary>Chunks available for immediate download.</summary>
    public required int ChunksAvailable { get; init; }

    /// <summary>Archive-tier chunks with ready rehydrated copies.</summary>
    public required int ChunksAlreadyRehydrated { get; init; }

    /// <summary>Archive-tier chunks that need rehydration.</summary>
    public required int ChunksNeedingRehydration { get; init; }

    /// <summary>Archive-tier chunks with rehydration already pending.</summary>
    public required int ChunksPendingRehydration { get; init; }

    /// <summary>Total compressed bytes that require rehydration or are already pending.</summary>
    public required long RehydrationBytes { get; init; }

    /// <summary>Total compressed bytes available for immediate download.</summary>
    public required long DownloadBytes { get; init; }

    // ── Per-component cost fields ─────────────────────────────────────────────

    /// <summary>Archive data retrieval cost at Standard priority.</summary>
    public required double RetrievalCostStandard { get; init; }

    /// <summary>Archive data retrieval cost at High priority.</summary>
    public required double RetrievalCostHigh { get; init; }

    /// <summary>Archive read operation cost at Standard priority.</summary>
    public required double ReadOpsCostStandard { get; init; }

    /// <summary>Archive read operation cost at High priority.</summary>
    public required double ReadOpsCostHigh { get; init; }

    /// <summary>Write operation cost for rehydrated chunk copies.</summary>
    public required double WriteOpsCost { get; init; }

    /// <summary>Storage cost for rehydrated chunk copies.</summary>
    public required double StorageCost { get; init; }

    // ── Computed totals ───────────────────────────────────────────────────────

    /// <summary>Total estimated cost at Standard priority.</summary>
    public double TotalStandard => RetrievalCostStandard + ReadOpsCostStandard + WriteOpsCost + StorageCost;

    /// <summary>Total estimated cost at High priority.</summary>
    public double TotalHigh => RetrievalCostHigh + ReadOpsCostHigh + WriteOpsCost + StorageCost;
}
