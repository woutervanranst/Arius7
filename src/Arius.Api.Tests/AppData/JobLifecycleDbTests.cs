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
}
