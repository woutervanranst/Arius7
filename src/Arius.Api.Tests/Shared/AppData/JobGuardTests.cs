using Arius.Api.Shared.AppData;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Arius.Api.Tests.Shared.AppData;

/// <summary>
/// Covers the single-active-job-per-repository guard: the cooperative <see cref="AppDatabase.HasActiveJob"/> check,
/// the <c>ux_jobs_one_active_per_repo</c> partial unique index that enforces it in SQLite, and
/// <see cref="AppDatabase.ReconcileInterruptedJobs"/>, which recovers jobs orphaned by a crash.
/// </summary>
public sealed class JobGuardTests
{
    /// <summary>A fresh on-disk database with one repository for job rows to reference.</summary>
    private static (AppDatabase Database, long RepositoryId) NewDatabase()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db");
        var database = new AppDatabase(path);
        var accountId = database.InsertAccount("acc", encryptedAccountKey: null);
        var repositoryId = database.InsertRepository("alias", "container", accountId, localPath: null, encryptedPassphrase: null);
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

    /// <summary>
    /// Two 'running' rows for one repository make creating <c>ux_jobs_one_active_per_repo</c> fail, so opening the
    /// database must first reconcile them to 'interrupted'; otherwise the Api cannot start.
    /// </summary>
    [Test]
    public async Task Reopening_a_database_with_two_pre_guard_running_rows_reconciles_before_the_index_is_created()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db");
        var seed = new AppDatabase(path);
        var accountId = seed.InsertAccount("acc", encryptedAccountKey: null);
        var repositoryId = seed.InsertRepository("alias", "container", accountId, localPath: null, encryptedPassphrase: null);
        seed.InsertJob("j1", repositoryId, "archive", "one-off", "running");

        // InsertJob rejects a second running row, so drop the index and insert it directly.
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            using var dropIndex = connection.CreateCommand();
            dropIndex.CommandText = "DROP INDEX ux_jobs_one_active_per_repo;";
            dropIndex.ExecuteNonQuery();

            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO jobs(id, repo_id, kind, trigger, status, pct, started_at)
                VALUES ('j2', $repoId, 'restore', 'one-off', 'running', 0, $startedAt);
                """;
            insert.Parameters.AddWithValue("$repoId", repositoryId);
            insert.Parameters.AddWithValue("$startedAt", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }

        var reopened = Should.NotThrow(() => new AppDatabase(path));

        var jobs = reopened.ListJobs();
        jobs.Single(j => j.Id == "j1").Status.ShouldBe("interrupted");
        jobs.Single(j => j.Id == "j2").Status.ShouldBe("interrupted");
    }
}
