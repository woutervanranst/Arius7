using System.Net.Http.Json;
using System.Text.Json;
using Arius.Api.Features.Jobs;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests.Features.Jobs;

public class LifecycleScenarioTests
{
    /// <summary><c>GET /jobs/{id}</c> reports the total warning count (<c>Snapshot.WarningCount</c>), not the number
    /// of warnings retained in the capped ring.</summary>
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
            TotalBytes = 0, TotalNewBytes = 0, ScannedBytes = 0, ScannedFiles = 0, HashedBytes = 0, UploadedBytes = 0,
            DedupedBytes = 0, DedupedFiles = 0, EtaSeconds = null, ThroughputBytesPerSec = 0, Pct = 100,
            WarningCount = 250,   // the persisted ring below holds only 50
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
        await Assert.That(detail!.WarningCount).IsEqualTo(250);
    }
}
