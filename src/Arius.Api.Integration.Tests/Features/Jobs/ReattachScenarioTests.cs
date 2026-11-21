using System.Net.Http.Json;
using System.Text.Json;
using Arius.Api.FakeTestHost;
using Arius.Api.Features.Jobs;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hubs;
using Arius.Api.Shared.Jobs;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.Cost;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests.Features.Jobs;

public class ReattachScenarioTests
{
    [Test]
    public async Task Awaiting_cost_job_surfaces_the_persisted_estimate_on_reattach()
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
                TotalStandard = 0.71, TotalHigh = 4.31, StandardWait = TimeSpan.FromHours(15), HighWait = TimeSpan.FromHours(1),
            },
            PostApproveEvents: [],
            Result: new RestoreResult { Success = true, FilesRestored = 0, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = null }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobId = Guid.NewGuid().ToString();
        // The run parks at awaiting-cost while still alive, blocked in the ConfirmRehydration callback, so its sink
        // stays in JobStateRegistry and the reattach below exercises the live path, not the persisted state.
        _ = runner.RunRestoreAsync(repoId, jobId, "test", null, [], false, false);
        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));

        // Reattach through GET /jobs/{id}, without a SignalR connection.
        var http = factory.CreateClient();
        var detail = await http.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");
        await Assert.That(detail.GetProperty("cost").ValueKind).IsNotEqualTo(JsonValueKind.Null);
        await Assert.That(detail.GetProperty("cost").GetProperty("totalHigh").GetDouble()).IsEqualTo(4.31);
        await Assert.That(detail.GetProperty("resume").GetProperty("autoResume").GetBoolean()).IsTrue();
        await Assert.That(detail.GetProperty("resume").GetProperty("rehydrationWindowHours").GetDouble()).IsEqualTo(15d);
        await Assert.That(detail.GetProperty("resume").GetProperty("priority").GetString()).IsEqualTo("Standard");
        await Assert.That(detail.GetProperty("resume").GetProperty("lastCheckedAt").GetDateTimeOffset())
            .IsEqualTo(detail.GetProperty("resume").GetProperty("rehydrationStartedAt").GetDateTimeOffset());

        // Release the blocked run.
        factory.Services.GetRequiredService<RestoreApprovalRegistry>().Resolve(jobId, null);
    }
}
