using Arius.Api.Shared.AppData;
using Shouldly;

namespace Arius.Api.Tests.Shared.AppData;

public sealed class DownloadStoreTests
{
    private static (AppDatabase Database, long RepositoryId) NewDatabase()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db");
        var database = new AppDatabase(path);
        var accountId = database.InsertAccount("acc", encryptedAccountKey: null);
        var repositoryId = database.InsertRepository("alias", "container", accountId, localPath: null, encryptedPassphrase: null);
        return (database, repositoryId);
    }

    [Test]
    public async Task Inserted_download_files_list_by_path_not_ready_and_not_downloaded()
    {
        var (db, repoId) = NewDatabase();
        db.InsertJob("job", repoId, "download", "one-off", "running");

        db.InsertDownloadFiles("job", [new("photos/b.jpg", 2), new("a.txt", 1)]);

        db.ListDownloadFiles("job").ShouldBe(
        [
            new DownloadFile("a.txt", 1, false, false),
            new DownloadFile("photos/b.jpg", 2, false, false),
        ]);
    }

    [Test]
    public async Task Marking_ready_and_downloaded_changes_only_the_named_files()
    {
        var (db, repoId) = NewDatabase();
        db.InsertJob("job", repoId, "download", "one-off", "running");
        db.InsertDownloadFiles("job", [new("photos/b.jpg", 2), new("a.txt", 1)]);

        db.MarkDownloadFileReady("job", "a.txt");
        db.MarkDownloadFilesDownloaded("job", ["a.txt"]);

        db.ListDownloadFiles("job").ShouldBe(
        [
            new DownloadFile("a.txt", 1, true, true),
            new DownloadFile("photos/b.jpg", 2, false, false),
        ]);
    }

    [Test]
    public async Task Download_settings_default_to_clean_up_after_7_days_and_round_trip()
    {
        var (db, _) = NewDatabase();

        db.GetDownloadSettings().ShouldBe(DownloadSettings.Default);
        DownloadSettings.Default.ShouldBe(new DownloadSettings(true, 7));

        db.SaveDownloadSettings(new DownloadSettings(false, 3));

        db.GetDownloadSettings().ShouldBe(new DownloadSettings(false, 3));
    }

    [Test]
    public async Task ListDownloadJobs_returns_only_download_jobs_of_the_repository_newest_first()
    {
        var (db, repoId) = NewDatabase();
        var otherAccountId = db.InsertAccount("acc2", null);
        var otherRepoId = db.InsertRepository("other", "container2", otherAccountId, null, null);

        db.InsertJob("old", repoId, "download", "one-off", "running");
        db.CompleteJob("old", "completed", 100, null);
        await Task.Delay(10);
        db.InsertJob("restore", repoId, "restore", "one-off", "running");
        db.CompleteJob("restore", "completed", 100, null);
        await Task.Delay(10);
        db.InsertJob("new", repoId, "download", "one-off", "running");
        db.InsertJob("elsewhere", otherRepoId, "download", "one-off", "running");

        db.ListDownloadJobs(repoId).Select(j => j.Id).ShouldBe(["new", "old"]);
        db.ListDownloadJobs().Select(j => j.Id).ToHashSet().ShouldBe(["new", "old", "elsewhere"], ignoreOrder: true);
    }

    [Test]
    public async Task Deleting_a_repository_removes_its_download_files()
    {
        var (db, repoId) = NewDatabase();
        db.InsertJob("job", repoId, "download", "one-off", "running");
        db.InsertDownloadFiles("job", [new("a.txt", 1)]);

        db.DeleteRepository(repoId);

        db.ListDownloadFiles("job").ShouldBeEmpty();
        db.GetJob("job").ShouldBeNull();
    }
}
