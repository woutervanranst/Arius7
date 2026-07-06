using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
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
}
