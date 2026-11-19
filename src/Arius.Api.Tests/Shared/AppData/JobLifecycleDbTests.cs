using Arius.Api.Shared.AppData;
using Arius.Api.Shared.Hubs;
using Shouldly;

namespace Arius.Api.Tests.Shared.AppData;

public sealed class JobLifecycleDbTests
{
    private static (AppDatabase Database, long RepositoryId) NewDatabase()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db"); // TODO scan codebase for GetTempPath and use canonical test paths
        var database = new AppDatabase(path);
        var accountId = database.InsertAccount("acc", encryptedAccountKey: null);
        var repositoryId = database.InsertRepository("alias", "container", accountId, localPath: null, encryptedPassphrase: null);
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
        var repo2 = db.InsertRepository("a2", "c2", acc2, null, null);
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
        var repo2 = db.InsertRepository("a2", "c2", acc2, null, null);
        db.InsertJob("rehy", repo2, "restore", "one-off", "running");
        db.SetJobStatus("rehy", "rehydrating");

        db.ReconcileInterruptedJobs();

        db.GetJob("await")!.Status.ShouldBe("interrupted");   // an orphaned cost prompt is terminal, which frees the guard
        db.HasActiveJob(repoId).ShouldBeFalse();
        db.GetJob("rehy")!.Status.ShouldBe("rehydrating");    // the poller resumes this one
    }

    [Test]
    public async Task CompleteJob_does_not_overwrite_an_already_terminal_row()
    {
        var (db, repoId) = NewDatabase();
        db.InsertJob("j", repoId, "restore", "one-off", "running");
        db.CompleteJob("j", "completed", 100, "Restore complete.");

        db.CompleteJob("j", "cancelled", 0, "Cancelled.");            // a racing cancel must be a no-op

        var job = db.GetJob("j")!;
        job.Status.ShouldBe("completed");
        job.Pct.ShouldBe(100d);
    }

    [Test]
    public async Task CompleteJob_finalizes_a_parked_awaiting_cost_row_with_finished_at()
    {
        // Declining a parked job completes it from "awaiting-cost" in one guarded call, which must set the
        // status, pct, detail and finished_at.
        var (db, repoId) = NewDatabase();
        db.InsertJob("j", repoId, "restore", "one-off", "running");
        db.SetJobStatus("j", "awaiting-cost", "Awaiting cost approval");

        db.CompleteJob("j", "cancelled", 0, "Cancelled.");

        var job = db.GetJob("j")!;
        job.Status.ShouldBe("cancelled");
        job.FinishedAt.ShouldNotBeNull();
        job.Detail.ShouldBe("Cancelled.");
    }
}
