using Arius.Core.Shared.ChunkIndex;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.Storage;

namespace Arius.Tests.Shared.Fakes;

/// <summary>
/// Deterministic <see cref="IStorageCostEstimator"/> for Core unit tests — no real pricing data, no cloud
/// dependency. Storage cost = stored GiB × <see cref="StorageRate"/>; restore total = restored GiB × a flat
/// rate (High adds an extra unit per rehydrated GiB so High &gt; Standard when archive is involved).
/// </summary>
public sealed class FakeStorageCostEstimator : IStorageCostEstimator
{
    private const double GiB = 1024.0 * 1024.0 * 1024.0;

    /// <summary>The region this estimator reports as having priced (the estimator is bound to one container).</summary>
    public string Region { get; init; } = "westeurope";

    /// <summary>Flat per-GiB-month storage rate per tier (test-controlled, deterministic).</summary>
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
        };
    }
}
