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
    public async Task GetJob_returns_null_for_unknown_id()
    {
        var (db, _) = NewDatabase();
        db.GetJob("nope").ShouldBeNull();
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
    public async Task GetJob_state_json_deserializes_to_persisted_state()
    {
        var (db, repoId) = NewDatabase();
        db.InsertJob("j1", repoId, "restore", "one-off", "running");
        var sink = new Arius.Api.Jobs.JobSink();
        sink.SetRestoreTotals(2, 2000);
        db.SaveJobState("j1", System.Text.Json.JsonSerializer.Serialize(
            sink.BuildPersistedState(DateTimeOffset.UnixEpoch, resume: null)));

        var job = db.GetJob("j1")!;
        var persisted = System.Text.Json.JsonSerializer.Deserialize<Arius.Api.Jobs.PersistedJobState>(job.StateJson!)!;
        persisted.Snapshot.RestoreTotalFiles.ShouldBe(2L);
        persisted.Warnings.ShouldNotBeNull();
    }
}
