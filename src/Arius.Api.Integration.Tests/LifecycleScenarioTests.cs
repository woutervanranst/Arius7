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
    /// <summary>Regression: <c>GET /jobs/{id}</c> must report the true total on <c>Snapshot.WarningCount</c>,
    /// not the ring-capped <c>Warnings.Count</c> (max 200) which undercounts any job with more than 200
    /// warnings. Persists a <see cref="PersistedJobState"/> with the >200 mismatch directly and asserts the
    /// endpoint reports the true total.</summary>
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
