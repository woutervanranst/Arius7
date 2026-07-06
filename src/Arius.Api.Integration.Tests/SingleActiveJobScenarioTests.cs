using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Testing;
using Arius.Api.Jobs;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class SingleActiveJobScenarioTests
{
    [Test]
    public async Task Restore_parked_at_awaiting_cost_blocks_new_jobs_until_cancelled()
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
        var approvals = factory.Services.GetRequiredService<RestoreApprovalRegistry>();
        var jobId = Guid.NewGuid().ToString();

        _ = runner.RunRestoreAsync(repoId, jobId, "test", null, [], false, false);
        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));

        // By design: the repo is busy while the restore is parked → HasActiveJob true, a new start is rejected.
        await Assert.That(db.HasActiveJob(repoId)).IsTrue();

        // Cancel the parked restore (decline) → frees the repo.
        approvals.Resolve(jobId, null);
        await ScenarioWait.Until(() => !db.HasActiveJob(repoId), TimeSpan.FromSeconds(10));
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("cancelled");

        // Now a new job for the repo is accepted (guard clear).
        await Assert.That(db.HasActiveJob(repoId)).IsFalse();
    }
}
