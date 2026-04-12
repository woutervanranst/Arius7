using Arius.Core.Shared;

namespace Arius.Core.Tests.Shared;

public class RepositoryPathsTests
{
    [Test]
    public void RepoDirectoryName_Format_IsAccountHyphenContainer()
    {
        var name = RepositoryPaths.GetRepoDirectoryName("mystorageacct", "photos");
        name.ShouldBe("mystorageacct-photos");
    }

    [Test]
    public void RepositoryDirectories_AreDerivedUnderUserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".arius", "account-container");

        RepositoryPaths.GetRepositoryDirectory("account", "container").ShouldBe(root);
        RepositoryPaths.GetChunkIndexCacheDirectory("account", "container").ShouldBe(Path.Combine(root, "chunk-index"));
        RepositoryPaths.GetFileTreeCacheDirectory("account", "container").ShouldBe(Path.Combine(root, "filetrees"));
        RepositoryPaths.GetSnapshotCacheDirectory("account", "container").ShouldBe(Path.Combine(root, "snapshots"));
        RepositoryPaths.GetLogsDirectory("account", "container").ShouldBe(Path.Combine(root, "logs"));
    }
}
