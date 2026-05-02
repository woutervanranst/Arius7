using Arius.Core.Shared.FileTree;
using Arius.Core.Shared.Hashes;

namespace Arius.Core.Tests.Shared.FileTree;

public class FileTreePathsTests
{
    [Test]
    public void GetCachePath_AppendsHashToCacheDirectory()
    {
        var cacheDir = Path.Combine("root", "filetrees");
        var hash = FileTreeHash.Parse(new string('a', 64));

        FileTreePaths.GetCachePath(cacheDir, hash)
            .ShouldBe(Path.Combine(cacheDir, hash.ToString()));
    }

    [Test]
    public void StagingPaths_AreDerivedUnderFileTreeCacheDirectory()
    {
        var cacheDir = Path.Combine("root", "filetrees");
        var directoryId = FileTreePaths.GetStagingDirectoryId("docs");

        FileTreePaths.GetStagingRootDirectory(cacheDir)
            .ShouldBe(Path.Combine(cacheDir, ".staging"));
        FileTreePaths.GetStagingLockPath(cacheDir)
            .ShouldBe(Path.Combine(cacheDir, ".staging.lock"));
        FileTreePaths.GetStagingNodePath(Path.Combine(cacheDir, ".staging"), directoryId)
            .ShouldBe(Path.Combine(cacheDir, ".staging", directoryId));
    }
}
