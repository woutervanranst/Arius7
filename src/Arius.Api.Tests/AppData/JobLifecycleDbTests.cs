using Arius.Api.AppData;
using Shouldly;

namespace Arius.Api.Tests.AppData;

public sealed class JobLifecycleDbTests
{
    private static (AppDatabase Database, long RepositoryId) NewDatabase()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db");
        var database = new AppDatabase(path);
        var accountId = database.InsertAccount("acc", encryptedAccountKey: null);
        var repositoryId = database.InsertRepository("alias", "container", accountId, localPath: null, "archive", encryptedPassphrase: null);
        return (database, repositoryId);
    }

    [Test]
    public async Task SetJobStatus_updates_status_without_finishing()
    {
        var (db, repoId) = NewDatabase();
        db.InsertJob("j1", repoId, "restore", "one-off", "running");

        db.SetJobStatus("j1", "rehydrating", "Waiting for rehydration");

        var job = db.GetJob("j1")!;
        job.Status.ShouldBe("rehydrating");
        job.Detail.ShouldBe("Waiting for rehydration");
        job.FinishedAt.ShouldBeNull();
        db.HasActiveJob(repoId).ShouldBeTrue();   // rehydrating is non-terminal
    }

    [Test]
    public async Task ListActiveRehydrations_returns_only_rehydrating_rows()
    {
        var (db, repoId) = NewDatabase();
        db.InsertJob("j1", repoId, "restore", "one-off", "running");
        db.SetJobStatus("j1", "rehydrating");

        var acc2 = db.InsertAccount("acc2", null);
        var repo2 = db.InsertRepository("a2", "c2", acc2, null, "archive", null);
        db.InsertJob("j2", repo2, "restore", "one-off", "running");   // stays running

        var rows = db.ListActiveRehydrations();
        rows.Count.ShouldBe(1);
        rows[0].Id.ShouldBe("j1");
    }

    [Test]
    public async Task ReconcileInterruptedJobs_marks_awaiting_cost_interrupted_but_leaves_rehydrating()
    {
        var (db, repoId) = NewDatabase();
        db.InsertJob("await", repoId, "restore", "one-off", "running");
        db.SetJobStatus("await", "awaiting-cost");

        var acc2 = db.InsertAccount("acc2", null);
        var repo2 = db.InsertRepository("a2", "c2", acc2, null, "archive", null);
        db.InsertJob("rehy", repo2, "restore", "one-off", "running");
        db.SetJobStatus("rehy", "rehydrating");

        db.ReconcileInterruptedJobs();

        db.GetJob("await")!.Status.ShouldBe("interrupted");   // orphaned cost prompt is terminal → guard freed
        db.HasActiveJob(repoId).ShouldBeFalse();
        db.GetJob("rehy")!.Status.ShouldBe("rehydrating");    // poller re-arms this one
    }

    [Test]
    public async Task CompleteJob_does_not_overwrite_an_already_terminal_row()
    {
        var (db, repoId) = NewDatabase();
        db.InsertJob("j", repoId, "restore", "one-off", "running");
        db.CompleteJob("j", "completed", 100, "Restore complete.");   // legitimate terminal transition

        db.CompleteJob("j", "cancelled", 0, "Cancelled.");            // racing cancel — must be a no-op now

        var job = db.GetJob("j")!;
        job.Status.ShouldBe("completed");   // NOT clobbered to cancelled
        job.Pct.ShouldBe(100d);             // pct not reset to 0
    }

    [Test]
    public async Task CompleteJob_finalizes_a_parked_awaiting_cost_row_with_finished_at()
    {
        // Regression for JobsHub.DeclineParkedAsync: it used to call SetJobStatus("cancelled") before the
        // guarded CompleteJob, which flipped the row terminal FIRST and made CompleteJob's guard no-op —
        // finished_at was never set. The fix drops that redundant call; a single guarded CompleteJob from the
        // still-non-terminal "awaiting-cost" status must finalize status + pct + detail + finished_at.
        var (db, repoId) = NewDatabase();
        db.InsertJob("j", repoId, "restore", "one-off", "running");
        db.SetJobStatus("j", "awaiting-cost", "Awaiting cost approval");

        db.CompleteJob("j", "cancelled", 0, "Cancelled.");

        var job = db.GetJob("j")!;
        job.Status.ShouldBe("cancelled");
        job.FinishedAt.ShouldNotBeNull();   // the bug left this null
        job.Detail.ShouldBe("Cancelled.");
    }
}
