using Arius.Api.AppData;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Arius.Api.Tests.AppData;

public sealed class LastArchiveRunTests
{
    [Test]
    public void ListLatestArchiveStartedAt_returns_latest_archive_per_repository()
    {
        var path = Path.Combine(Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db");
        var database = new AppDatabase(path);
        var accountId = database.InsertAccount("acc", encryptedAccountKey: null);
        var repositoryId = database.InsertRepository("alias", "container", accountId, localPath: null, "archive", encryptedPassphrase: null);
        var otherRepositoryId = database.InsertRepository("other", "other-container", accountId, localPath: null, "archive", encryptedPassphrase: null);
        var emptyRepositoryId = database.InsertRepository("empty", "empty-container", accountId, localPath: null, "archive", encryptedPassphrase: null);
        var olderArchive = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
        var failedArchive = new DateTimeOffset(2026, 11, 10, 11, 0, 0, TimeSpan.Zero);
        var latestArchive = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        var laterRestore = new DateTimeOffset(2026, 11, 11, 13, 0, 0, TimeSpan.Zero);

        database.InsertJob("archive-old", repositoryId, "archive", "one-off", "completed");
        database.InsertJob("archive-failed", repositoryId, "archive", "one-off", "failed");
        database.InsertJob("archive-active", repositoryId, "archive", "one-off", "running");
        database.InsertJob("restore-later", repositoryId, "restore", "one-off", "completed");
        database.InsertJob("other-archive", otherRepositoryId, "archive", "schedule", "completed");

        SetStartedAt(path, "archive-old", olderArchive);
        SetStartedAt(path, "archive-failed", failedArchive);
        SetStartedAt(path, "archive-active", latestArchive);
        SetStartedAt(path, "restore-later", laterRestore);
        SetStartedAt(path, "other-archive", failedArchive);

        var starts = database.ListLatestArchiveStartedAt();

        starts[repositoryId].ShouldBe(latestArchive);
        starts[otherRepositoryId].ShouldBe(failedArchive);
        starts.ContainsKey(emptyRepositoryId).ShouldBeFalse();
        var repositoryStarts = database.ListLatestArchiveStartedAt(repositoryId);
        repositoryStarts.Count.ShouldBe(1);
        repositoryStarts[repositoryId].ShouldBe(latestArchive);
    }

    private static void SetStartedAt(string path, string jobId, DateTimeOffset startedAt)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET started_at = $startedAt WHERE id = $id;";
        command.Parameters.AddWithValue("$startedAt", startedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", jobId);
        command.ExecuteNonQuery();
    }
}
