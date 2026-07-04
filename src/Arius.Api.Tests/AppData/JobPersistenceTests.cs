using Arius.Api.AppData;
using Shouldly;

namespace Arius.Api.Tests.AppData;

/// <summary>
/// Covers persistence of a job's live state (<see cref="AppDatabase.SaveJobState"/>) and terminal
/// outcome (<see cref="AppDatabase.SetJobOutcome"/>): both round-trip through <see cref="AppDatabase.ListJobs"/>
/// via <see cref="JobRecord.StateJson"/> / <see cref="JobRecord.Outcome"/>.
/// </summary>
public sealed class JobPersistenceTests
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
    public async Task State_and_outcome_persist_and_read_back()
    {
        var (database, repositoryId) = NewDatabase();
        database.InsertJob("j1", repositoryId, "archive", "one-off", "running");

        database.SaveJobState("j1", "{\"phase\":\"upload\"}");
        database.SetJobOutcome("j1", "{\"fileCount\":3}");

        var job = database.ListJobs().Single(j => j.Id == "j1");
        job.StateJson.ShouldBe("{\"phase\":\"upload\"}");
        job.Outcome.ShouldBe("{\"fileCount\":3}");
    }
}
