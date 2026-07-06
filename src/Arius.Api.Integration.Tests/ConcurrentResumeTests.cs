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
    // Smoke test for concurrent ResumeRestoreAsync of the same job: it verifies the fixed code path does not
    // deadlock, does not double-complete, and leaks no registry entry under real concurrency.
    //
    // It does NOT discriminate the review-#3 clobber (it passes with and without the Register-under-gate fix):
    // the fix's correctness is verified by inspection instead. An automated discriminator is impractical —
    // once the first resume flips status to "running" under the repo gate, a second resume bails at the
    // pre-gate (rehydrating|awaiting-cost) guard and never reaches Register, so the clobber exists only in a
    // sub-microsecond thread race that scenario-gating can't force, and the ux_jobs_one_active_per_repo unique
    // index prevents holding the repo gate open with a second job to widen the window.
    [Test]
    public async Task Concurrent_resumes_of_the_same_job_complete_cleanly_without_deadlock_or_leak()
    {
        await using var factory = new AriusApiFactory();
        var dest = Path.Combine(Path.GetTempPath(), $"arius-itest-dst-{Guid.NewGuid():N}");
        var repoId = factory.SeedRepository(localPath: dest);

        factory.Scenarios.SetRestore(repoId, new RestoreScenario(
            PreCostEvents: [ new FileRestoredEvent(RelativePath.Parse("a"), 100) ],
            CostPrompt: null,
            PostApproveEvents: [],
            Result: new RestoreResult { Success = true, FilesRestored = 1, FilesSkipped = 0, ChunksPendingRehydration = 0, ErrorMessage = null }));

        var runner = factory.Services.GetRequiredService<JobRunner>();
        var db = factory.Services.GetRequiredService<AppDatabase>();
        var jobStates = factory.Services.GetRequiredService<JobStateRegistry>();

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

        // Genuinely interleave the two resumes.
        var a = Task.Run(() => runner.ResumeRestoreAsync(jobId));
        var b = Task.Run(() => runner.ResumeRestoreAsync(jobId));
        await Task.WhenAll(a, b);

        await Assert.That(jobStates.TryGet(jobId, out _)).IsFalse();          // no leaked registration
        await Assert.That(db.GetJob(jobId)!.Status).IsEqualTo("completed");   // single clean completion
    }
}
