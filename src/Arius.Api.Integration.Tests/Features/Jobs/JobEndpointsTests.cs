using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arius.Api.Features.Jobs;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests.Features.Jobs;

/// <summary>The jobs history (<c>GET /jobs</c>, filtered by repository and status) and a job's warnings tail
/// (<c>GET /jobs/{id}/warnings</c>).</summary>
public class JobEndpointsTests
{
    [Test]
    public async Task The_job_history_lists_every_job_with_its_repository_and_filters_by_repository_and_status()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var itest = factory.SeedRepository();
        var other = db.InsertRepository("other", "other-container", db.InsertAccount("other-account", null), localPath: null, encryptedPassphrase: null);

        db.InsertJob("itest-done", itest, "archive", "one-off", "running");
        db.CompleteJob("itest-done", "completed", 100, "Archive complete");
        db.SetJobOutcome("itest-done", """{"fileCount":3}""");
        db.InsertJob("itest-failed", itest, "restore", "schedule", "failed");
        db.InsertJob("itest-running", itest, "archive", "one-off", "running");
        db.InsertJob("other-parked", other, "restore", "one-off", "rehydrating");
        db.InsertJob("other-cancelled", other, "download", "one-off", "cancelled");

        var all = (await client.GetFromJsonAsync<List<JobDto>>("/api/jobs"))!;

        await Assert.That(all.Select(j => j.Id)).IsEquivalentTo(["itest-done", "itest-failed", "itest-running", "other-parked", "other-cancelled"]);
        var done = all.Single(j => j.Id == "itest-done");
        var doneRow = db.GetJob("itest-done")!;
        await Assert.That(done).IsEqualTo(new JobDto("itest-done", itest, "itest", "archive", "one-off", "completed", 100, "Archive complete",
            doneRow.StartedAt, doneRow.FinishedAt, """{"fileCount":3}"""));
        await Assert.That(all.Single(j => j.Id == "other-parked").Repo).IsEqualTo("other");
        await Assert.That(await IdsAsync(client, "status=")).IsEquivalentTo(all.Select(j => j.Id));

        await Assert.That(await IdsAsync(client, $"repositoryId={itest}")).IsEquivalentTo(["itest-done", "itest-failed", "itest-running"]);
        await Assert.That(await IdsAsync(client, "status=failed")).IsEquivalentTo(["itest-failed"]);
        await Assert.That(await IdsAsync(client, "status=terminal")).IsEquivalentTo(["itest-done", "itest-failed", "other-cancelled"]);
        await Assert.That(await IdsAsync(client, $"status=terminal&repositoryId={other}")).IsEquivalentTo(["other-cancelled"]);
        await Assert.That(await IdsAsync(client, "status=active")).IsEquivalentTo(["itest-running", "other-parked"]);
        await Assert.That(await IdsAsync(client, $"status=active&repositoryId={other}")).IsEquivalentTo(["other-parked"]);

        static async Task<List<string>> IdsAsync(HttpClient client, string query) =>
            (await client.GetFromJsonAsync<List<JobDto>>($"/api/jobs?{query}"))!.Select(j => j.Id).ToList();
    }

    [Test]
    public async Task Warnings_return_the_retained_tail_and_say_when_more_were_emitted()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var repoId = factory.SeedRepository();
        db.InsertJob("j-warn", repoId, "archive", "one-off", "completed");
        var tail = Enumerable.Range(50, 200).Select(i => $"warn {i}").ToArray();
        db.SaveJobState("j-warn", JsonSerializer.Serialize(new PersistedJobState
        {
            Snapshot = new JobSink("j-warn", null).BuildSnapshot(DateTimeOffset.UtcNow) with { WarningCount = 250 },
            Warnings = tail,
        }));

        var warnings = await client.GetFromJsonAsync<JobWarningsDto>("/api/jobs/j-warn/warnings");

        await Assert.That(warnings!.Count).IsEqualTo(250);
        await Assert.That(warnings.Lines).IsEquivalentTo(tail);
        await Assert.That(warnings.Truncated).IsTrue();
    }

    [Test]
    public async Task A_job_without_recorded_state_has_no_warnings_and_an_unknown_job_is_404()
    {
        await using var factory = new AriusApiFactory();
        using var client = factory.CreateClient();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        db.InsertJob("j-plain", factory.SeedRepository(), "archive", "one-off", "completed");

        var warnings = await client.GetFromJsonAsync<JobWarningsDto>("/api/jobs/j-plain/warnings");
        using var unknown = await client.GetAsync("/api/jobs/nope/warnings");

        await Assert.That(warnings!.Count).IsEqualTo(0);
        await Assert.That(warnings.Lines).IsEmpty();
        await Assert.That(warnings.Truncated).IsFalse();
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
