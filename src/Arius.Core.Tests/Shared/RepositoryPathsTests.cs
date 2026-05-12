using Arius.Core.Shared;
using Arius.Tests.Shared;

namespace Arius.Core.Tests.Shared;

public class RepositoryPathsTests
{
    [Test]
    public void RepositoryDirectories_AreDerivedUnderUserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".arius", "account-container");

        RepositoryPaths.GetRepositoryRoot("account", "container").ToString().ShouldBe(root);
        RepositoryPaths.GetRepositoryDirectoryName("account", "container").ShouldBe(PathSegment.Parse("account-container"));
        RepositoryPaths.GetChunkIndexCacheRoot("account", "container").ToString().ShouldBe(Path.Combine(root, "chunk-index"));
        RepositoryPaths.GetFileTreeCacheRoot("account", "container").ToString().ShouldBe(Path.Combine(root,   "filetrees"));
        RepositoryPaths.GetSnapshotCacheRoot("account", "container").ToString().ShouldBe(Path.Combine(root,   "snapshots"));
        RepositoryPaths.GetLogsRoot("account", "container").ToString().ShouldBe(Path.Combine(root, "logs"));
    }

    [Test]
    public void RepositoryCacheHelpers_ExposeTypedRootsAndRelativeSegments()
    {
        RepositoryPaths.GetRepositoryRoot("account", "container").ToString()
            .ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".arius", "account-container"));
        RepositoryPaths.GetRepositoryDirectoryName("account", "container")
            .ShouldBe(PathSegment.Parse("account-container"));
        RepositoryPaths.GetRepoDirectoryName("account", "container")
            .ShouldBe(PathSegment.Parse("account-container"));

        RepositoryPaths.GetChunkIndexCacheRoot("account", "container").ToString()
            .ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".arius", "account-container", "chunk-index"));
        RepositoryPaths.GetFileTreeCacheRoot("account", "container").ToString()
            .ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".arius", "account-container", "filetrees"));
        RepositoryPaths.GetSnapshotCacheRoot("account", "container").ToString()
            .ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".arius", "account-container", "snapshots"));
        RepositoryPaths.GetLogsRoot("account", "container").ToString()
            .ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".arius", "account-container", "logs"));
    }
}
