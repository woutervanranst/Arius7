using System.Net.Http.Json;
using System.Text.Json;
using Arius.Api.FakeTestHost;
using Arius.Api.Features.Jobs;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Jobs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests.Features.Restore;

/// <summary>The hub's restore methods: <c>StartRestore</c>, answering the cost prompt (<c>ApproveRestore</c> /
/// <c>DeclineRestore</c>) and driving a parked rehydrating restore (<c>SetAutoResume</c> / <c>ResumeRestore</c>).</summary>
public class RestoreHubTests
{
    [Test]
    public async Task StartRestore_streams_the_cost_estimate_to_the_caller_and_a_high_approval_parks_with_the_high_rehydration_window()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository(localPath: TempDestination());
        factory.Scenarios.SetRestore(repoId, CanonicalScenarios.RehydratingRestoreStaysPending());
        await using var conn = factory.CreateHubConnection();
        var cost = new TaskCompletionSource<CostEstimateDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<CostEstimateDto>("CostEstimate", c => cost.TrySetResult(c));
        await conn.StartAsync();

        var jobId = await conn.InvokeAsync<string>("StartRestore", repoId, null, null, false, false);

        var estimate = await cost.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(estimate.JobId).IsEqualTo(jobId);
        await Assert.That(estimate.TotalHigh).IsEqualTo(4.31);

        await conn.InvokeAsync("ApproveRestore", jobId, "High");

        var resume = await ParkedResumeAsync(factory, jobId);
        await Assert.That(resume.GetProperty("priority").GetString()).IsEqualTo("High");
        await Assert.That(resume.GetProperty("rehydrationWindowHours").GetDouble()).IsEqualTo(1d);
    }

    [Test]
    public async Task An_approval_without_a_priority_proceeds_at_standard_rather_than_declining()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository(localPath: TempDestination());
        factory.Scenarios.SetRestore(repoId, CanonicalScenarios.RehydratingRestoreStaysPending());
        var db = factory.Services.GetRequiredService<AppDatabase>();
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        var jobId = await conn.InvokeAsync<string>("StartRestore", repoId, null, null, false, false);
        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));
        await conn.InvokeAsync("ApproveRestore", jobId, (string?)null);

        var resume = await ParkedResumeAsync(factory, jobId);
        await Assert.That(resume.GetProperty("priority").GetString()).IsEqualTo("Standard");
        await Assert.That(resume.GetProperty("rehydrationWindowHours").GetDouble()).IsEqualTo(15d);
    }

    [Test]
    public async Task StartRestore_is_refused_while_the_repository_has_an_active_job()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        db.InsertJob("j-running", repoId, "archive", "one-off", "running");
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        var ex = await Assert.ThrowsAsync<HubException>(() => conn.InvokeAsync<string>("StartRestore", repoId, null, null, false, false));

        await Assert.That(ex!.Message).Contains("A job is already running for this repository.");
        await Assert.That(db.ListJobs().Select(j => j.Id)).IsEquivalentTo(["j-running"]);
    }

    [Test]
    public async Task DeclineRestore_cancels_a_restore_awaiting_cost()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository(localPath: TempDestination());
        var scenario = CanonicalScenarios.RehydratingRestore();
        factory.Scenarios.SetRestore(repoId, scenario);
        var db = factory.Services.GetRequiredService<AppDatabase>();
        await using var conn = factory.CreateHubConnection();
        var done = OnDone(conn);
        await conn.StartAsync();

        var jobId = await conn.InvokeAsync<string>("StartRestore", repoId, null, null, false, false);
        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));
        await conn.InvokeAsync("DeclineRestore", jobId);

        await Assert.That(await done.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo("cancelled");
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("cancelled");
    }

    [Test]
    public async Task DeclineRestore_of_a_parked_restore_marks_it_cancelled()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobId = SeedParkedRestore(db, repoId, TempDestination(), autoResume: true);
        await using var conn = factory.CreateHubConnection();
        var done = OnDone(conn);
        await conn.StartAsync();
        await conn.InvokeAsync<JobAttachState?>("AttachToJob", jobId);

        await conn.InvokeAsync("DeclineRestore", jobId);

        await Assert.That(await done.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo("cancelled");
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("cancelled");
    }

    [Test]
    public async Task Turning_auto_resume_off_keeps_a_rehydrating_restore_parked_for_a_manual_resume()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobId = SeedParkedRestore(db, repoId, TempDestination(), autoResume: true);
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        await conn.InvokeAsync("SetAutoResume", jobId, false);

        using var client = factory.CreateClient();
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");
        await Assert.That(detail.GetProperty("status").GetString()).IsEqualTo("rehydrating");
        await Assert.That(detail.GetProperty("resume").GetProperty("autoResume").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Turning_auto_resume_on_re_drives_the_parked_restore_to_completion()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var scenario = CanonicalScenarios.OnlineRestore();
        factory.Scenarios.SetRestore(repoId, scenario);
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var destination = TempDestination();
        var jobId = SeedParkedRestore(db, repoId, destination, autoResume: false);
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        await conn.InvokeAsync("SetAutoResume", jobId, true);

        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "completed", TimeSpan.FromSeconds(10));
        await Assert.That(scenario.Received.Single().Options.RootDirectory).IsEqualTo(destination);
    }

    [Test]
    public async Task ResumeRestore_re_drives_a_parked_restore_now()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var scenario = CanonicalScenarios.OnlineRestore();
        factory.Scenarios.SetRestore(repoId, scenario);
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobId = SeedParkedRestore(db, repoId, TempDestination(), autoResume: false);
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        await conn.InvokeAsync("ResumeRestore", jobId);

        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "completed", TimeSpan.FromSeconds(10));
        await Assert.That(scenario.Received.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SetAutoResume_leaves_a_job_without_resume_state_untouched()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        db.InsertJob("j-no-state", repoId, "restore", "one-off", "completed");
        db.InsertJob("j-no-resume", repoId, "restore", "one-off", "completed");
        db.SaveJobState("j-no-resume", JsonSerializer.Serialize(new PersistedJobState
        {
            Snapshot = new JobSink("j-no-resume", null).BuildSnapshot(DateTimeOffset.UtcNow),
            Warnings = [],
        }));
        db.InsertJob("j-garbled", repoId, "restore", "one-off", "completed");
        db.SaveJobState("j-garbled", "not json");
        var before = new[] { "j-no-state", "j-no-resume", "j-garbled" }.ToDictionary(id => id, id => db.GetJob(id)!.StateJson);
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        foreach (var jobId in before.Keys.Append("j-unknown"))
            await conn.InvokeAsync("SetAutoResume", jobId, true);

        foreach (var (jobId, stateJson) in before)
            await Assert.That(db.GetJob(jobId)!.StateJson).IsEqualTo(stateJson);
        await Assert.That(db.GetJob("j-unknown")).IsNull();
    }

    private static string TempDestination() => Path.Combine(Path.GetTempPath(), $"arius-itest-dst-{Guid.NewGuid():N}");

    /// <summary>A restore parked at <c>rehydrating</c> with its resume parameters and no live run, as between poller ticks.</summary>
    private static string SeedParkedRestore(AppDatabase db, long repoId, string destination, bool autoResume)
    {
        var jobId = Guid.NewGuid().ToString();
        db.InsertJob(jobId, repoId, "restore", "one-off", "rehydrating");
        db.SaveJobState(jobId, JsonSerializer.Serialize(new PersistedJobState
        {
            Snapshot = new JobSink(jobId, null).BuildSnapshot(DateTimeOffset.UtcNow),
            Warnings = [],
            Resume = new RestoreResumeState
            {
                Version = null, TargetPaths = [], Destination = destination, Overwrite = false, NoPointers = false,
                Priority = "Standard", AutoResume = autoResume, RehydrationStartedAt = DateTimeOffset.UtcNow,
                LastRunAt = DateTimeOffset.UtcNow, RehydrationWindow = TimeSpan.FromHours(15),
            },
        }));
        return jobId;
    }

    /// <summary>Waits until the run parks at <c>rehydrating</c> and ends, so the view comes from the persisted state,
    /// then returns the job's <c>resume</c> from <c>GET /jobs/{id}</c>.</summary>
    private static async Task<JsonElement> ParkedResumeAsync(AriusApiFactory factory, string jobId)
    {
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobStates = factory.Services.GetRequiredService<JobStateRegistry>();
        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "rehydrating" && !jobStates.TryGet(jobId, out _), TimeSpan.FromSeconds(10));

        using var client = factory.CreateClient();
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");
        return detail.GetProperty("resume").Clone();
    }

    /// <summary>Completes with the status of the first <c>Done</c> message, which carries one camelCase object.</summary>
    private static TaskCompletionSource<string> OnDone(HubConnection conn)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("Done", msg => done.TrySetResult(msg.GetProperty("status").GetString()!));
        return done;
    }
}
