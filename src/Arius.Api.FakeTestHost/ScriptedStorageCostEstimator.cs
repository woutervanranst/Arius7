using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;

namespace Arius.Api.FakeTestHost;

/// <summary>Deterministic <see cref="IStorageCostEstimator"/> for the scripted host — no pricing data, no cloud.
/// Identical arithmetic to the Core unit-test fake (kept independent so the shipped-only-in-test-host graph
/// never references Arius.Tests.Shared's TUnit/NSubstitute/Azurite deps).</summary>
public sealed class ScriptedStorageCostEstimator : IStorageCostEstimator
{
    private const double GiB = 1024.0 * 1024.0 * 1024.0;

    public string Region { get; init; } = "westeurope";

    public double StorageRate(BlobTier tier) => tier switch
    {
        BlobTier.Hot     => 0.02,
        BlobTier.Cool    => 0.01,
        BlobTier.Cold    => 0.004,
        BlobTier.Archive => 0.001,
        _                => 0.0,
    };

    public StorageCostEstimate EstimateStorageCost(IReadOnlyList<ChunkTierStatistic> storedByTier)
    {
        var tiers = storedByTier
            .Select(t => new TierStorageCost(t.Tier, t.UniqueChunks, t.StoredSize, t.StoredSize / GiB * StorageRate(t.Tier)))
            .ToList();
        return new StorageCostEstimate(Region, tiers, tiers.Sum(t => t.CostPerMonth));
    }

    public RestoreCostEstimate EstimateRestoreCost(RestoreCostRequest request)
    {
        var restoredGiB = (request.DownloadBytes + request.BytesNeedingRehydration) / GiB;
        var rehydrateGiB = request.BytesNeedingRehydration / GiB;
        return new RestoreCostEstimate
        {
            ChunksAvailable          = request.ChunksAvailable,
            ChunksAlreadyRehydrated  = request.ChunksAlreadyRehydrated,
            ChunksNeedingRehydration = request.ChunksNeedingRehydration,
            ChunksPendingRehydration = request.ChunksPendingRehydration,
            BytesNeedingRehydration  = request.BytesNeedingRehydration,
            BytesPendingRehydration  = request.BytesPendingRehydration,
            DownloadBytes            = request.DownloadBytes,
            TotalStandard            = restoredGiB,
            TotalHigh                = restoredGiB + rehydrateGiB,
            StandardWait             = TimeSpan.FromHours(15),
            HighWait                 = TimeSpan.FromHours(1),
        };
    }
}
