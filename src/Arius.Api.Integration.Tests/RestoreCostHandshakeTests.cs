using Arius.Api.AppData;
using Arius.Api.FakeTestHost;
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
        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));
        approvals.Resolve(jobId, RehydratePriority.High);

        await run;
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("completed");

        // The persisted outcome is handed to the web client verbatim (GET /jobs + the SignalR Done) and
        // parsed there as a camelCase JobOutcome. Default System.Text.Json options emit PascalCase, which
        // made outcome.filesRestored read as undefined → 0 on the client (the restore-roundtrip
        // regression). Guard the casing at the point it is produced.
        var outcome = db.GetJob(jobId)!.Outcome;
        await Assert.That(outcome).IsNotNull();
        await Assert.That(outcome!.Contains("\"filesRestored\":3")).IsTrue();
        await Assert.That(outcome!.Contains("\"FilesRestored\"")).IsFalse();
    }

    [Test]
    public async Task Multiple_target_paths_run_as_one_restore_with_one_cost_prompt()
    {
        await using var factory = new AriusApiFactory();
        var dest = Path.Combine(Path.GetTempPath(), $"arius-itest-dst-{Guid.NewGuid():N}");
        var repoId = factory.SeedRepository(localPath: dest);

        var scenario = CanonicalScenarios.RehydratingRestore() with
        {
            PostApproveEvents =
            [
                new FileRestoredEvent(RelativePath.Parse("photos/a.jpg"), 1000),
                new FileRestoredEvent(RelativePath.Parse("docs/b.txt"), 1000),
            ],
            Result = new RestoreResult { Success = true, FilesRestored = 2, FilesSkipped = 0, ChunksPendingRehydration = 0 },
        };
        factory.Scenarios.SetRestore(repoId, scenario);

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var approvals = factory.Services.GetRequiredService<RestoreApprovalRegistry>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobId = Guid.NewGuid().ToString();

        var run = runner.RunRestoreAsync(repoId, jobId, connectionId: "test", version: null,
            targetPaths: ["photos", "docs/b.txt"], overwrite: false, noPointers: false);

        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));
        approvals.Resolve(jobId, RehydratePriority.Standard);

        await run;
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("completed");
        // One restore over both targets: each file is counted once, not once per target.
        await Assert.That(scenario.Received.Count).IsEqualTo(1);
        await Assert.That(scenario.Received.Single().Options.TargetPaths.SequenceEqual([RelativePath.Parse("photos"), RelativePath.Parse("docs/b.txt")])).IsTrue();
        await Assert.That(db.GetJob(jobId)!.Outcome!.Contains("\"filesRestored\":2")).IsTrue();
    }
}
