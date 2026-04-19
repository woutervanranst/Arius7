using System.Threading.Tasks;
using System.Linq;
using Arius.Explorer.Settings;

namespace Arius.Explorer.Tests.Settings;

public class RecentRepositoryManagerTests
{
    [Test]
    public void RecentRepositoryManager_TouchOrAdd_AddsAndOrdersMostRecentFirst()
    {
        var settings = new FakeApplicationSettings();
        var manager = new RecentRepositoryManager(settings);

        var repoA = new RepositoryOptions
        {
            LocalDirectoryPath = "C:/a",
            AccountName = "acct",
            ContainerName = "repo-a",
        };
        var repoB = new RepositoryOptions
        {
            LocalDirectoryPath = "C:/b",
            AccountName = "acct",
            ContainerName = "repo-b",
        };

        manager.TouchOrAdd(repoA);
        Task.Delay(5).GetAwaiter().GetResult();
        manager.TouchOrAdd(repoB);

        manager.GetAll().Count.ShouldBe(2);
        manager.GetMostRecent().ShouldBe(repoB);
        settings.SaveCalls.ShouldBe(2);
    }

    [Test]
    public void RecentRepositoryManager_TouchOrAdd_UpdatesExistingWithoutDuplicating()
    {
        var settings = new FakeApplicationSettings();
        var manager = new RecentRepositoryManager(settings);

        var original = new RepositoryOptions
        {
            LocalDirectoryPath = "C:/data",
            AccountName = "acct",
            ContainerName = "repo",
            AccountKeyProtected = "old-key",
            PassphraseProtected = "old-pass",
        };
        var updated = new RepositoryOptions
        {
            LocalDirectoryPath = "C:/data",
            AccountName = "acct",
            ContainerName = "repo",
            AccountKeyProtected = "new-key",
            PassphraseProtected = "new-pass",
        };

        manager.TouchOrAdd(original);
        manager.TouchOrAdd(updated);

        settings.RecentRepositories.Count.ShouldBe(1);
        settings.RecentRepositories[0].AccountKeyProtected.ShouldBe("new-key");
        settings.RecentRepositories[0].PassphraseProtected.ShouldBe("new-pass");
    }

    [Test]
    public void RecentRepositoryManager_Remove_RemovesMatchingRepositories()
    {
        var settings = new FakeApplicationSettings();
        var manager = new RecentRepositoryManager(settings);

        settings.RecentRepositories.Add(new RepositoryOptions { LocalDirectoryPath = "C:/a", AccountName = "acct", ContainerName = "keep" });
        settings.RecentRepositories.Add(new RepositoryOptions { LocalDirectoryPath = "C:/b", AccountName = "acct", ContainerName = "remove" });

        manager.Remove(repo => repo.ContainerName == "remove");

        settings.RecentRepositories.Count.ShouldBe(1);
        settings.RecentRepositories[0].ContainerName.ShouldBe("keep");
        settings.SaveCalls.ShouldBe(1);
    }

    [Test]
    public void RecentRepositoryManager_TouchOrAdd_RespectsRecentLimit()
    {
        var settings = new FakeApplicationSettings { RecentLimit = 2 };
        var manager = new RecentRepositoryManager(settings);

        manager.TouchOrAdd(new RepositoryOptions { LocalDirectoryPath = "C:/a", AccountName = "acct", ContainerName = "repo-a" });
        Task.Delay(5).GetAwaiter().GetResult();
        manager.TouchOrAdd(new RepositoryOptions { LocalDirectoryPath = "C:/b", AccountName = "acct", ContainerName = "repo-b" });
        Task.Delay(5).GetAwaiter().GetResult();
        manager.TouchOrAdd(new RepositoryOptions { LocalDirectoryPath = "C:/c", AccountName = "acct", ContainerName = "repo-c" });

        manager.GetAll().Select(repo => repo.ContainerName).ShouldBe(["repo-c", "repo-b"]);
    }

    [Test]
    public void RecentRepositoryManager_GetMostRecent_WhenEmpty_ReturnsNull()
    {
        var settings = new FakeApplicationSettings();
        var manager = new RecentRepositoryManager(settings);

        manager.GetMostRecent().ShouldBeNull();
    }

    [Test]
    public void RecentRepositoryManager_Remove_WhenNothingMatches_DoesNotSave()
    {
        var settings = new FakeApplicationSettings();
        var manager = new RecentRepositoryManager(settings);

        settings.RecentRepositories.Add(new RepositoryOptions { LocalDirectoryPath = "C:/a", AccountName = "acct", ContainerName = "keep" });

        manager.Remove(repo => repo.ContainerName == "missing");

        settings.RecentRepositories.Count.ShouldBe(1);
        settings.SaveCalls.ShouldBe(0);
    }
}
