using Arius.Core.Shared.Cost;
using Arius.Tests.Shared.Fakes;

namespace Arius.Core.Tests.Cost;

public class RestoreCostEstimateWaitTests
{
    [Test]
    public async Task Estimate_carries_rehydration_wait_windows()
    {
        var estimator = new FakeStorageCostEstimator();
        var estimate = estimator.EstimateRestoreCost(new RestoreCostRequest
        {
            ChunksNeedingRehydration = 3,
            BytesNeedingRehydration  = 3_000_000,
            DownloadBytes            = 1_000_000,
        });

        await Assert.That(estimate.StandardWait).IsEqualTo(TimeSpan.FromHours(15));
        await Assert.That(estimate.HighWait).IsEqualTo(TimeSpan.FromHours(1));
    }
}
