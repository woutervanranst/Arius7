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
}
