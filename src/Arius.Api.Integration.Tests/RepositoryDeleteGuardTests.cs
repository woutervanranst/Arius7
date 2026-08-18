using System.Net;
using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

/// <summary>
/// DELETE /repos/{id} must refuse while a job is active: deleting disposes the repository's logger factory
/// (closing its log file), which a concurrent job build resolves loggers from — so the two must not overlap.
/// </summary>
public class RepositoryDeleteGuardTests
{
    [Test]
    public async Task Delete_is_rejected_with_conflict_while_a_job_is_active_and_succeeds_once_it_ends()
    {
        await using var factory = new AriusApiFactory();
        var client = factory.CreateClient();
        var db     = factory.Services.GetRequiredService<AppDatabase>();

        var repoId = factory.SeedRepository();
        var jobId  = Guid.NewGuid().ToString();
        db.InsertJob(jobId, repoId, "archive", "one-off", "running");

        var blocked = await client.DeleteAsync($"/api/repos/{repoId}");
        await Assert.That(blocked.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(db.GetRepository(repoId)).IsNotNull();

        db.CompleteJob(jobId, "completed", 100, "done");
        await Assert.That(db.HasActiveJob(repoId)).IsFalse();

        var allowed = await client.DeleteAsync($"/api/repos/{repoId}");
        await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(db.GetRepository(repoId)).IsNull();
    }
}
