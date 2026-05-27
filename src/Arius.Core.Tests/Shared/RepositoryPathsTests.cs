using Arius.Core.Shared;

namespace Arius.Core.Tests.Shared;

public class RepositoryPathsTests
{
    [Test]
    public void RepositoryDirectories_AreDerivedUnderUserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".arius", "account-container");

        RepositoryLocalStatePaths.GetRepositoryRoot("account", "container").ToString().ShouldBe(root);
        RepositoryLocalStatePaths.GetChunkIndexCacheRoot("account", "container").ToString().ShouldBe(Path.Combine(root, "chunk-index"));
        RepositoryLocalStatePaths.GetFileTreeCacheRoot("account", "container").ToString().ShouldBe(Path.Combine(root,   "filetrees"));
        RepositoryLocalStatePaths.GetSnapshotCacheRoot("account", "container").ToString().ShouldBe(Path.Combine(root,   "snapshots"));
        RepositoryLocalStatePaths.GetLogsDirectory("account", "container").ToString().ShouldBe(Path.Combine(root, "logs"));
    }

    [Test]
    public void RepositoryCacheHelpers_ExposeTypedRootsAndRelativeSegments()
    {
        RepositoryLocalStatePaths.GetRepositoryRoot("account", "container").ToString()
            .ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".arius", "account-container"));

        RepositoryLocalStatePaths.GetChunkIndexCacheRoot("account", "container").ToString()
            .ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".arius", "account-container", "chunk-index"));
        RepositoryLocalStatePaths.GetFileTreeCacheRoot("account", "container").ToString()
            .ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".arius", "account-container", "filetrees"));
        RepositoryLocalStatePaths.GetSnapshotCacheRoot("account", "container").ToString()
            .ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".arius", "account-container", "snapshots"));
        RepositoryLocalStatePaths.GetLogsDirectory("account", "container").ToString()
            .ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".arius", "account-container", "logs"));
    }
}
