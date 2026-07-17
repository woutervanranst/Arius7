using Arius.Api.AppData;
using Arius.Api.FakeTestHost;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Arius.Api.Integration.Tests;

public class StaleApprovalSweepTests
{
    [Test]
    public async Task Sweep_cancels_an_abandoned_awaiting_cost_job_and_frees_the_repo()
    {
        await using var factory = new AriusApiFactory();
        var dest = Path.Combine(Path.GetTempPath(), $"arius-itest-dst-{Guid.NewGuid():N}");
        var repoId = factory.SeedRepository(localPath: dest);

        factory.Scenarios.SetRestore(repoId, new RestoreScenario(
            PreCostEvents:
            [
                new SnapshotResolvedEvent(DateTimeOffset.UnixEpoch, default),
                new TreeTraversalCompleteEvent(FileCount: 1, TotalOriginalSize: 100),
                new ChunkResolutionCompleteEvent(TotalChunks: 2, LargeCount: 1, TarCount: 0, TotalChunkBytes: 100),
                new RehydrationStatusEvent(Available: 0, Rehydrated: 0, NeedsRehydration: 2, Pending: 0),
            ],
            CostPrompt: new RestoreCostEstimate
            {
                ChunksAvailable = 0, ChunksAlreadyRehydrated = 0, ChunksNeedingRehydration = 2, ChunksPendingRehydration = 0,
                BytesNeedingRehydration = 100, BytesPendingRehydration = 0, DownloadBytes = 100,
                TotalStandard = 0.5, TotalHigh = 2.0, StandardWait = TimeSpan.FromHours(15), HighWait = TimeSpan.FromHours(1),
            },
            PostApproveEvents: [],
            Result: new RestoreResult { Success = true, FilesRestored = 0, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = null }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobId = Guid.NewGuid().ToString();

        _ = runner.RunRestoreAsync(repoId, jobId, "test", null, [], false, false);
        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));

        var sweep = new StaleApprovalSweepService(factory.Services, factory.Services.GetRequiredService<ILogger<StaleApprovalSweepService>>());
        sweep.Sweep(cutoff: DateTimeOffset.UtcNow.AddMinutes(1));   // treat the just-started job as stale

        await ScenarioWait.Until(() => !db.HasActiveJob(repoId), TimeSpan.FromSeconds(10));
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("cancelled");
    }
}
