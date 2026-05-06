using Arius.Core.Shared;
using Arius.Core.Shared.FileSystem;

namespace Arius.Core.Tests.Shared;

public class RepositoryPathsTests
{
    [Test]
    public void RepositoryDirectories_AreDerivedUnderUserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, ".arius", "account-container");

        RepositoryPaths.GetRepositoryDirectory("account", "container").ShouldBe(root);
        RepositoryPaths.GetRepoDirectoryName("account", "container").ShouldBe("account-container");
        RepositoryPaths.GetChunkIndexCacheDirectory("account", "container").ShouldBe(Path.Combine(root, "chunk-index"));
        RepositoryPaths.GetFileTreeCacheDirectory("account", "container").ShouldBe(Path.Combine(root,   "filetrees"));
        RepositoryPaths.GetSnapshotCacheDirectory("account", "container").ShouldBe(Path.Combine(root,   "snapshots"));
        RepositoryPaths.GetLogsDirectory("account", "container").ShouldBe(Path.Combine(root,            "logs"));
    }

    [Test]
    public void RepositoryCacheHelpers_ExposeTypedRootsAndRelativeSegments()
    {
        RepositoryPaths.GetRepositoryRoot("account", "container").ToString()
            .ShouldBe(RepositoryPaths.GetRepositoryDirectory("account", "container"));
        RepositoryPaths.ChunkIndexCacheRelativePath.ShouldBe(RelativePath.Parse("chunk-index"));
        RepositoryPaths.FileTreeCacheRelativePath.ShouldBe(RelativePath.Parse("filetrees"));
        RepositoryPaths.SnapshotCacheRelativePath.ShouldBe(RelativePath.Parse("snapshots"));
        RepositoryPaths.LogsRelativePath.ShouldBe(RelativePath.Parse("logs"));

        RepositoryPaths.GetChunkIndexCacheRoot("account", "container").ToString()
            .ShouldBe(RepositoryPaths.GetChunkIndexCacheDirectory("account", "container"));
        RepositoryPaths.GetFileTreeCacheRoot("account", "container").ToString()
            .ShouldBe(RepositoryPaths.GetFileTreeCacheDirectory("account", "container"));
        RepositoryPaths.GetSnapshotCacheRoot("account", "container").ToString()
            .ShouldBe(RepositoryPaths.GetSnapshotCacheDirectory("account", "container"));
        RepositoryPaths.GetLogsRoot("account", "container").ToString()
            .ShouldBe(RepositoryPaths.GetLogsDirectory("account", "container"));
    }
}
