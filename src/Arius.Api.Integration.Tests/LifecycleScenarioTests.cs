using System.Net.Http.Json;
using System.Text.Json;
using Arius.Api.AppData;
using Arius.Api.Contracts;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class LifecycleScenarioTests
{
    /// <summary>Regression for fix #4: a cancel that arrives after the rehydration poller has already completed
    /// a job must not clobber the completed row. <see cref="Arius.Api.Hubs.JobsHub"/> is not resolvable from DI
    /// (SignalR instantiates it per-connection), but its <c>CancelJob</c> fall-through branch funnels straight
    /// into the guarded <see cref="AppDatabase.CompleteJob"/> with no branching in between — exercising the DB
    /// guard directly against the app's own <see cref="AppDatabase"/> instance is the meaningful regression check.</summary>
    [Test]
    public async Task Cancel_after_completion_does_not_clobber_the_completed_row()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        db.InsertJob("j", repoId, "restore", "one-off", "running");
        db.CompleteJob("j", "completed", 100, "Restore complete.");   // poller completed it

        // Cancel arrives after completion: JobsHub.CancelJob's fall-through calls the guarded CompleteJob.
        db.CompleteJob("j", "cancelled", 0, "Cancelled.");

        var job = db.GetJob("j");
        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Status).IsEqualTo("completed");
        await Assert.That(job.Pct).IsEqualTo(100d);
    }

    /// <summary>Regression for fix #12: <c>GET /jobs/{id}</c> used the ring-capped <c>Warnings.Count</c> (max 200)
    /// instead of the true total on <c>Snapshot.WarningCount</c>, undercounting any job with more than 200
    /// warnings. A scripted restore emitting 250 warnings is heavier than needed to exercise the endpoint's
    /// read path, so this persists a <see cref="PersistedJobState"/> with the >200 mismatch directly (mirroring
    /// what the ring capping would have produced) and asserts the endpoint reports the true total.</summary>
    [Test]
    public async Task Detail_endpoint_reports_the_true_warning_count_not_the_ring_cap()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        db.InsertJob("j-warn", repoId, "restore", "one-off", "completed");
        db.CompleteJob("j-warn", "completed", 100, "Restore complete.");

        var snapshot = new JobSnapshot
        {
            JobId = "j-warn", Phase = "done",
            TotalBytes = 0, TotalNewBytes = 0, ScannedBytes = 0, HashedBytes = 0, UploadedBytes = 0,
            DedupedBytes = 0, DedupedFiles = 0, EtaSeconds = null, ThroughputBytesPerSec = 0, Pct = 100,
            WarningCount = 250,   // true total; the ring below only retains the last 50 lines
            Stats = new Dictionary<string, string>(),
        };
        var persisted = new PersistedJobState
        {
            Snapshot = snapshot,
            Warnings = Enumerable.Range(0, 50).Select(i => $"warn {i}").ToArray(),
            Resume = null,
        };
        db.SaveJobState("j-warn", JsonSerializer.Serialize(persisted));

        using var client = factory.CreateClient();
        var detail = await client.GetFromJsonAsync<JobDetailDto>(
            "/api/jobs/j-warn", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        await Assert.That(detail).IsNotNull();
        await Assert.That(detail!.WarningCount).IsEqualTo(250);   // not 50 (the persisted ring's Count)
    }
}
