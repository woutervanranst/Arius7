using System.Text.Json;
using Arius.Api.FakeTestHost;
using Arius.Api.Features.Jobs;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Jobs;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests.Features.Jobs;

/// <summary>The hub's job methods: <c>AttachToJob</c>, which returns the current state and joins the job's group, and
/// <c>CancelJob</c> for a job awaiting cost approval, a running job and a parked job.</summary>
public class JobHubTests
{
    [Test]
    public async Task AttachToJob_of_an_unknown_job_returns_nothing()
    {
        await using var factory = new AriusApiFactory();
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        var state = await conn.InvokeAsync<JobAttachState?>("AttachToJob", "nope");

        await Assert.That(state).IsNull();
    }

    [Test]
    public async Task AttachToJob_of_a_finished_job_returns_its_persisted_progress()
    {
        await using var factory = new AriusApiFactory();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        db.InsertJob("j-done", factory.SeedRepository(), "archive", "one-off", "completed");
        db.SaveJobState("j-done", JsonSerializer.Serialize(new PersistedJobState
        {
            Snapshot = new JobSink("j-done", null).BuildSnapshot(DateTimeOffset.UtcNow) with { Phase = "done", Pct = 100, WarningCount = 2 },
            Warnings = ["first", "second"],
        }));
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        var state = await conn.InvokeAsync<JobAttachState?>("AttachToJob", "j-done");

        await Assert.That(state!.Status).IsEqualTo("completed");
        await Assert.That(state.Snapshot.JobId).IsEqualTo("j-done");
        await Assert.That(state.Snapshot.Phase).IsEqualTo("done");
        await Assert.That(state.Snapshot.Pct).IsEqualTo(100);
        await Assert.That(state.WarningCount).IsEqualTo(2);
        await Assert.That(state.Cost).IsNull();
        await Assert.That(state.Resume).IsNull();
    }

    [Test]
    public async Task AttachToJob_of_a_job_with_no_recorded_progress_returns_an_empty_snapshot()
    {
        await using var factory = new AriusApiFactory();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        db.InsertJob("j-bare", factory.SeedRepository(), "archive", "one-off", "interrupted");
        await using var conn = factory.CreateHubConnection();
        await conn.StartAsync();

        var state = await conn.InvokeAsync<JobAttachState?>("AttachToJob", "j-bare");

        await Assert.That(state!.Status).IsEqualTo("interrupted");
        await Assert.That(state.Snapshot.JobId).IsEqualTo("j-bare");
        await Assert.That(state.Snapshot.Phase).IsEqualTo("unknown");
        await Assert.That(state.Snapshot.Pct).IsEqualTo(0);
        await Assert.That(state.Snapshot.Stats).IsEmpty();
        await Assert.That(state.WarningCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_client_attached_to_a_restore_awaiting_cost_sees_the_estimate_and_its_cancellation()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository(localPath: Path.Combine(Path.GetTempPath(), $"arius-itest-dst-{Guid.NewGuid():N}"));
        factory.Scenarios.SetRestore(repoId, CanonicalScenarios.RehydratingRestore());
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobId = Guid.NewGuid().ToString();
        var run = factory.Services.GetRequiredService<JobRunner>().RunRestoreAsync(repoId, jobId, "test", null, [], false, false);
        await ScenarioWait.Until(() => db.GetJob(jobId)?.Status == "awaiting-cost", TimeSpan.FromSeconds(10));

        await using var conn = factory.CreateHubConnection();
        var done = OnDone(conn);
        await conn.StartAsync();

        var state = await conn.InvokeAsync<JobAttachState?>("AttachToJob", jobId);

        await Assert.That(state!.Status).IsEqualTo("awaiting-cost");
        await Assert.That(state.Cost!.JobId).IsEqualTo(jobId);
        await Assert.That(state.Cost.ChunksNeedingRehydration).IsEqualTo(282);
        await Assert.That(state.Cost.TotalHigh).IsEqualTo(4.31);
        await Assert.That(state.Resume!.Priority).IsEqualTo("Standard");
        await Assert.That(state.Resume.AutoResume).IsTrue();

        await conn.InvokeAsync("CancelJob", jobId);

        await Assert.That(await done.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo("cancelled");
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("cancelled");
    }

    [Test]
    public async Task CancelJob_stops_a_running_archive()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        factory.Scenarios.SetArchive(repoId, CanonicalScenarios.RepresentativeArchive(gated: true));
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobStates = factory.Services.GetRequiredService<JobStateRegistry>();
        await using var conn = factory.CreateHubConnection();
        var done = OnDone(conn);
        await conn.StartAsync();

        try
        {
            var jobId = await conn.InvokeAsync<string>("StartArchive", repoId, "Archive", false, false, false);
            await ScenarioWait.Until(() => jobStates.TryGet(jobId, out _), TimeSpan.FromSeconds(10));

            await conn.InvokeAsync("CancelJob", jobId);

            await Assert.That(await done.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo("cancelled");
            await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("cancelled");
        }
        finally
        {
            factory.Services.GetRequiredService<ScenarioGate>().Release(repoId);
        }
    }

    [Test]
    public async Task CancelJob_of_a_parked_restore_marks_it_cancelled_and_tells_attached_clients()
    {
        await using var factory = new AriusApiFactory();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var repoId = factory.SeedRepository();
        db.InsertJob("j-parked", repoId, "restore", "one-off", "rehydrating");   // no live run, as between poller ticks
        await using var conn = factory.CreateHubConnection();
        var done = OnDone(conn);
        await conn.StartAsync();
        await Assert.That((await conn.InvokeAsync<JobAttachState?>("AttachToJob", "j-parked"))!.Status).IsEqualTo("rehydrating");

        await conn.InvokeAsync("CancelJob", "j-parked");

        await Assert.That(await done.Task.WaitAsync(TimeSpan.FromSeconds(30))).IsEqualTo("cancelled");
        await Assert.That(db.GetJob("j-parked")!.Status).IsEqualTo("cancelled");
        await Assert.That(db.HasActiveJob(repoId)).IsFalse();
    }

    /// <summary>Completes with the status of the first <c>Done</c> message, which carries one camelCase object.</summary>
    private static TaskCompletionSource<string> OnDone(HubConnection conn)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("Done", msg => done.TrySetResult(msg.GetProperty("status").GetString()!));
        return done;
    }
}
