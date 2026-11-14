using Arius.Api.AppData;
using Arius.Api.Jobs;
using Shouldly;

namespace Arius.Api.Tests.Jobs;

public sealed class SchedulerServiceTests
{
    private static (AppDatabase Database, long RepositoryId) NewDatabaseWithRepository()
    {
        var path         = Path.Combine(Path.GetTempPath(), $"arius-api-tests-{Guid.NewGuid():N}.db");
        var database     = new AppDatabase(path);
        var accountId    = database.InsertAccount("acc", encryptedAccountKey: null);
        var repositoryId = database.InsertRepository("music", "container-music", accountId, localPath: null, encryptedPassphrase: null);
        return (database, repositoryId);
    }

    [Test]
    public async Task Scheduled_archive_uses_factory_defaults_when_nothing_is_set()
    {
        var (database, repositoryId) = NewDatabaseWithRepository();

        SchedulerService.ScheduledArchiveArgs(database, repositoryId).ShouldBe(("archive", false, false, false));
    }

    [Test]
    public async Task Scheduled_archive_combines_global_on_disk_with_repository_fast_hash()
    {
        var (database, repositoryId) = NewDatabaseWithRepository();
        database.ReplaceGlobalDefaults(DefaultValues.None with { OnDisk = "replace" });
        database.ReplaceRepositoryDefaults(repositoryId, DefaultValues.None with { FastHash = true });

        SchedulerService.ScheduledArchiveArgs(database, repositoryId).ShouldBe(("archive", true, true, true));
    }

    [Test]
    public async Task Scheduled_archive_uses_the_repository_tier()
    {
        var (database, repositoryId) = NewDatabaseWithRepository();
        database.ReplaceRepositoryDefaults(repositoryId, DefaultValues.None with { Tier = "cool" });

        SchedulerService.ScheduledArchiveArgs(database, repositoryId).Tier.ShouldBe("cool");
    }
}
