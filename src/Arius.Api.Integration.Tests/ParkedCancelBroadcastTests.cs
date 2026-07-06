using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class ParkedCancelBroadcastTests
{
    [Test]
    public async Task CancelParked_marks_the_job_cancelled()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var runner = factory.Services.GetRequiredService<JobRunner>();

        var jobId = Guid.NewGuid().ToString();
        db.InsertJob(jobId, repoId, "restore", "one-off", "rehydrating");

        runner.CancelParked(jobId);

        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("cancelled");
        await Assert.That(db.HasActiveJob(repoId)).IsFalse();
    }

    [Test]
    public async Task CancelParked_does_not_resurrect_an_already_completed_job()
    {
        await using var factory = new AriusApiFactory();
        var repoId = factory.SeedRepository();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var runner = factory.Services.GetRequiredService<JobRunner>();

        var jobId = Guid.NewGuid().ToString();
        db.InsertJob(jobId, repoId, "restore", "one-off", "rehydrating");
        await Assert.That(db.CompleteJob(jobId, "completed", 100, "Restore complete.")).IsTrue();   // poller wins
        await Assert.That(db.CompleteJob(jobId, "cancelled", 0, "late")).IsFalse();                 // guard: no-op on terminal

        runner.CancelParked(jobId);   // late cancel must not clobber

        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("completed");
    }
}
