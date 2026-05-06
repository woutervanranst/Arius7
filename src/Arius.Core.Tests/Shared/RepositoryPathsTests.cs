using Arius.Core.Shared;
using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;
using Arius.Core.Shared.Paths;
using Arius.Core.Shared.Snapshot;

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
        ((LocalRootPath)chunkIndexDirectoryMethod.Invoke(null, ["account", "container"])!).ShouldBe(root.GetSubdirectoryRoot("chunk-index"));
        ((LocalRootPath)fileTreeDirectoryMethod.Invoke(null, ["account", "container"])!).ShouldBe(root.GetSubdirectoryRoot("filetrees"));
        ((LocalRootPath)snapshotDirectoryMethod.Invoke(null, ["account", "container"])!).ShouldBe(root.GetSubdirectoryRoot("snapshots"));
        ((LocalRootPath)logsDirectoryMethod.Invoke(null, ["account", "container"])!).ShouldBe(root.GetSubdirectoryRoot("logs"));
    }

    [Test]
    public void TypedPathHelpers_DoNotExposeStringCompatibilityShortcuts()
    {
        typeof(LocalRootPath)
            .GetMethod("op_Implicit", [typeof(LocalRootPath)])
            .ShouldBeNull();

        typeof(SnapshotService)
            .GetMethod(nameof(SnapshotService.GetDiskCacheDirectory))!
            .ReturnType
            .ShouldBe(typeof(LocalRootPath));

        typeof(FileTreeStagingSession)
            .GetMethod(nameof(FileTreeStagingSession.OpenAsync), [typeof(string), typeof(CancellationToken)])
            .ShouldBeNull();

        typeof(FileTreePaths)
            .GetMethod(nameof(FileTreePaths.GetCachePath), [typeof(string), typeof(string)])
            .ShouldBeNull();

        typeof(FileTreePaths)
            .GetMethod(nameof(FileTreePaths.GetCachePath), [typeof(string), typeof(Arius.Core.Shared.Hashes.FileTreeHash)])
            .ShouldBeNull();
    }

    [Test]
    public void FileTreeService_UsesTypedFileTreeHashCachePaths()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(repoRoot, "src", "Arius.Core", "Shared", "FileTree", "FileTreeService.cs"));

        source.ShouldNotContain("FileTreePaths.GetCachePath(_diskCacheDir, hash.ToString())");
        source.ShouldNotContain("FileTreePaths.GetCachePath(_diskCacheDir, payload.Hash.ToString())");
        source.ShouldContain("FileTreePaths.GetCachePath(_diskCacheDir, hash)");
        source.ShouldContain("FileTreePaths.GetCachePath(_diskCacheDir, payload.Hash)");
    }
}
