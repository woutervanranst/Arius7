using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Arius.Core.Shared.FileSystem;
using Arius.Core.Shared.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class RestoreCostHandshakeTests
{
    [Test]
    public async Task Approving_the_cost_prompt_lets_the_restore_complete()
    {
        await using var factory = new AriusApiFactory();
        var dest = Path.Combine(Path.GetTempPath(), $"arius-itest-dst-{Guid.NewGuid():N}");
        var repoId = factory.SeedRepository(localPath: dest);

        factory.Scenarios.SetRestore(repoId, new RestoreScenario(
            PreCostEvents:
            [
                new SnapshotResolvedEvent(DateTimeOffset.UnixEpoch, default),
                new TreeTraversalCompleteEvent(FileCount: 3, TotalOriginalSize: 3000),
                new ChunkResolutionCompleteEvent(TotalChunks: 5, LargeCount: 1, TarCount: 1, TotalChunkBytes: 3000),
                new RehydrationStatusEvent(Available: 3, Rehydrated: 0, NeedsRehydration: 2, Pending: 0),
            ],
            CostPrompt: new RestoreCostEstimate
            {
                ChunksAvailable = 3, ChunksAlreadyRehydrated = 0, ChunksNeedingRehydration = 2, ChunksPendingRehydration = 0,
                BytesNeedingRehydration = 1200, BytesPendingRehydration = 0, DownloadBytes = 3000,
                TotalStandard = 0.71, TotalHigh = 4.31,
                StandardWait = TimeSpan.FromHours(15), HighWait = TimeSpan.FromHours(1),
            },
            PostApproveEvents:
            [
                new FileRestoredEvent(RelativePath.Parse("a"), 1000),
                new FileRestoredEvent(RelativePath.Parse("b"), 1000),
                new FileRestoredEvent(RelativePath.Parse("c"), 1000),
            ],
            Result: new RestoreResult
            {
                Success = true, FilesRestored = 3, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = null,
            }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var approvals = factory.Services.GetRequiredService<RestoreApprovalRegistry>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobId = Guid.NewGuid().ToString();

        var run = runner.RunRestoreAsync(repoId, jobId, connectionId: "test", version: null,
            targetPaths: [], overwrite: false, noPointers: false);

        // Wait for the job to park at awaiting-cost, then approve High priority.
        await WaitUntil(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));
        approvals.Resolve(jobId, RehydratePriority.High);

        await run;
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("completed");
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Condition not met within timeout.");
    }
}
