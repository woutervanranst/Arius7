using Arius.Api.AppData;
using Arius.Api.Integration.Tests.Harness;
using Arius.Api.Jobs;
using Arius.Api.Testing;
using Arius.Core.Features.RestoreCommand;
using Arius.Core.Shared.FileSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Arius.Api.Integration.Tests;

public class ConcurrentResumeTests
{
    [Test]
    public async Task Two_concurrent_resumes_do_not_leave_the_job_registered_after_both_finish()
    {
        await using var factory = new AriusApiFactory();
        var dest = Path.Combine(Path.GetTempPath(), $"arius-itest-dst-{Guid.NewGuid():N}");
        var repoId = factory.SeedRepository(localPath: dest);

        // A restore that completes (no pending) so each resume runs to completion.
        factory.Scenarios.SetRestore(repoId, new RestoreScenario(
            PreCostEvents: [ new FileRestoredEvent(RelativePath.Parse("a"), 100) ],
            CostPrompt: null,
            PostApproveEvents: [],
            Result: new RestoreResult { Success = true, FilesRestored = 1, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = null }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobStates = factory.Services.GetRequiredService<JobStateRegistry>();

        // Seed a rehydrating row with resume state so ResumeRestoreAsync proceeds.
        var jobId = Guid.NewGuid().ToString();
        db.InsertJob(jobId, repoId, "restore", "one-off", "rehydrating");
        db.SaveJobState(jobId, System.Text.Json.JsonSerializer.Serialize(new PersistedJobState
        {
            Snapshot = new JobSink(jobId, null!).BuildSnapshot(DateTimeOffset.UtcNow),
            Warnings = [],
            Resume = new RestoreResumeState
            {
                Version = null, TargetPaths = [], Destination = dest, Overwrite = false, NoPointers = false,
                Priority = "Standard", AutoResume = true, RehydrationStartedAt = DateTimeOffset.UtcNow,
                LastRunAt = DateTimeOffset.UtcNow, RehydrationWindow = TimeSpan.FromHours(15),
            },
        }));

        var a = runner.ResumeRestoreAsync(jobId);
        var b = runner.ResumeRestoreAsync(jobId);
        await Task.WhenAll(a, b);

        // Both runs finished: the registry must not hold a stale sink, and the job is terminal exactly once.
        await Assert.That(jobStates.TryGet(jobId, out _)).IsFalse();
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("completed");
    }
}
