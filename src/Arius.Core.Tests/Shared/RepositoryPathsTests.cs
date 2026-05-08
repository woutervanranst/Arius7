using Arius.Core.Shared;
using Arius.Core.Shared.FileSystem;
using Arius.Tests.Shared;

namespace Arius.Core.Tests.Shared;

public class RepositoryPathsTests
{
    [Test]
    public void RepositoryDirectories_AreDerivedUnderUserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".arius", "account-container");

        RepositoryCachePaths.GetRepositoryDirectory("account", "container").ShouldBe(root);
        RepositoryPaths.GetRepositoryDirectoryName("account", "container").ShouldBe(PathSegment.Parse("account-container"));
        RepositoryCachePaths.GetChunkIndexCacheDirectory("account", "container").ShouldBe(Path.Combine(root, "chunk-index"));
        RepositoryCachePaths.GetFileTreeCacheDirectory("account", "container").ShouldBe(Path.Combine(root,   "filetrees"));
        RepositoryCachePaths.GetSnapshotCacheDirectory("account", "container").ShouldBe(Path.Combine(root,   "snapshots"));
        RepositoryPaths.GetLogsRoot("account", "container").ToString().ShouldBe(Path.Combine(root, "logs"));
    }

    [Test]
    public void RepositoryCacheHelpers_ExposeTypedRootsAndRelativeSegments()
    {
        RepositoryPaths.GetRepositoryRoot("account", "container").ToString()
            .ShouldBe(RepositoryCachePaths.GetRepositoryDirectory("account", "container"));
        RepositoryPaths.GetRepositoryDirectoryName("account", "container")
            .ShouldBe(PathSegment.Parse("account-container"));
        RepositoryPaths.GetRepoDirectoryName("account", "container")
            .ShouldBe(PathSegment.Parse("account-container"));

        RepositoryPaths.GetChunkIndexCacheRoot("account", "container").ToString()
            .ShouldBe(RepositoryCachePaths.GetChunkIndexCacheDirectory("account", "container"));
        RepositoryPaths.GetFileTreeCacheRoot("account", "container").ToString()
            .ShouldBe(RepositoryCachePaths.GetFileTreeCacheDirectory("account", "container"));
        RepositoryPaths.GetSnapshotCacheRoot("account", "container").ToString()
            .ShouldBe(RepositoryCachePaths.GetSnapshotCacheDirectory("account", "container"));
        RepositoryPaths.GetLogsRoot("account", "container").ToString()
            .ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".arius", "account-container", "logs"));

        typeof(RepositoryPaths).GetMethod("GetLogsDirectory")
            .ShouldBeNull();
    }
}
