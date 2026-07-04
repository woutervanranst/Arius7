using Arius.Api.AppData;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Arius.Api.Tests.AppData;

/// <summary>
/// Covers the single-active-job-per-repository guard: <see cref="AppDatabase.HasActiveJob"/> (the
/// cooperative check used by the start paths), the <c>ux_jobs_one_active_per_repo</c> partial unique
/// index (the race-proof backstop enforced by SQLite itself), and <see cref="AppDatabase.ReconcileInterruptedJobs"/>
/// (restart recovery for jobs orphaned by a crash).
/// </summary>
public sealed class JobGuardTests
{
    /// <summary>A fresh on-disk database with one account + repository (the FK target for a job row),
    /// mirroring <c>StatisticsCacheTests.NewDatabase</c>.</summary>
    private static (AppDatabase Database, long RepositoryId) NewDatabase()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db");
        var database = new AppDatabase(path);
        var accountId = database.InsertAccount("acc", encryptedAccountKey: null);
        var repositoryId = database.InsertRepository("alias", "container", accountId, localPath: null, "archive", encryptedPassphrase: null);
        return (database, repositoryId);
    }

    [Test]
    public async Task HasActiveJob_true_while_running_false_when_terminal()
    {
        var (database, repositoryId) = NewDatabase();
        database.InsertJob("j1", repositoryId, "archive", "one-off", "running");

        database.HasActiveJob(repositoryId).ShouldBeTrue();

        database.CompleteJob("j1", "completed", 100, null);

        database.HasActiveJob(repositoryId).ShouldBeFalse();
    }

    [Test]
    public async Task Second_active_job_for_same_repo_is_rejected_by_the_index()
    {
        var (database, repositoryId) = NewDatabase();
        database.InsertJob("j1", repositoryId, "archive", "one-off", "running");

        Should.Throw<SqliteException>(() => database.InsertJob("j2", repositoryId, "restore", "one-off", "running"));
    }

    [Test]
    public async Task ReconcileInterruptedJobs_marks_orphaned_running_as_interrupted()
    {
        var (database, repositoryId) = NewDatabase();
        database.InsertJob("j1", repositoryId, "archive", "one-off", "running");

        var n = database.ReconcileInterruptedJobs();

        n.ShouldBe(1);
        database.ListJobs().Single(j => j.Id == "j1").Status.ShouldBe("interrupted");
    }
}
