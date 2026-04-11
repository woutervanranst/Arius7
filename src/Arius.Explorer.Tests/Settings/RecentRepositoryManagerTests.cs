using Arius.Explorer.Settings;
using Shouldly;
using System.Threading.Tasks;

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
}
