using Arius.Core.Shared;
using Arius.Core.Shared.Paths;

namespace Arius.Core.Tests.Shared;

public class RepositoryPathsTests
{
    [Test]
    public void RepositoryDirectories_AreDerivedUnderUserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = LocalRootPath.Parse(Path.Combine(home, ".arius", "account-container"));
        var repositoryDirectoryMethod = typeof(RepositoryPaths).GetMethod(nameof(RepositoryPaths.GetRepositoryDirectory))!;
        var chunkIndexDirectoryMethod = typeof(RepositoryPaths).GetMethod(nameof(RepositoryPaths.GetChunkIndexCacheDirectory))!;
        var fileTreeDirectoryMethod = typeof(RepositoryPaths).GetMethod(nameof(RepositoryPaths.GetFileTreeCacheDirectory))!;
        var snapshotDirectoryMethod = typeof(RepositoryPaths).GetMethod(nameof(RepositoryPaths.GetSnapshotCacheDirectory))!;
        var logsDirectoryMethod = typeof(RepositoryPaths).GetMethod(nameof(RepositoryPaths.GetLogsDirectory))!;

        repositoryDirectoryMethod.ReturnType.ShouldBe(typeof(LocalRootPath));
        chunkIndexDirectoryMethod.ReturnType.ShouldBe(typeof(LocalRootPath));
        fileTreeDirectoryMethod.ReturnType.ShouldBe(typeof(LocalRootPath));
        snapshotDirectoryMethod.ReturnType.ShouldBe(typeof(LocalRootPath));
        logsDirectoryMethod.ReturnType.ShouldBe(typeof(LocalRootPath));

        ((LocalRootPath)repositoryDirectoryMethod.Invoke(null, ["account", "container"])!).ShouldBe(root);
        ((LocalRootPath)chunkIndexDirectoryMethod.Invoke(null, ["account", "container"])!).ShouldBe(LocalRootPath.Parse(Path.Combine(root.ToString(), "chunk-index")));
        ((LocalRootPath)fileTreeDirectoryMethod.Invoke(null, ["account", "container"])!).ShouldBe(LocalRootPath.Parse(Path.Combine(root.ToString(), "filetrees")));
        ((LocalRootPath)snapshotDirectoryMethod.Invoke(null, ["account", "container"])!).ShouldBe(LocalRootPath.Parse(Path.Combine(root.ToString(), "snapshots")));
        ((LocalRootPath)logsDirectoryMethod.Invoke(null, ["account", "container"])!).ShouldBe(LocalRootPath.Parse(Path.Combine(root.ToString(), "logs")));
    }

    [Test]
    public void TypedPathHelpers_DoNotExposeStringCompatibilityShortcuts()
    {
        typeof(LocalRootPath)
            .GetMethod("op_Implicit", [typeof(LocalRootPath)])
            .ShouldBeNull();
    }
}
