using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests.Shared.Jobs;

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
        await Assert.That(db.CompleteJob(jobId, "completed", 100, "Restore complete.")).IsTrue();   // the poller completes it first
        await Assert.That(db.CompleteJob(jobId, "cancelled", 0, "late")).IsFalse();                 // completing a terminal job is a no-op

        runner.CancelParked(jobId);   // a late cancel must not overwrite the outcome

        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("completed");
    }
}
