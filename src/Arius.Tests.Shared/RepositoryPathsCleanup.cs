using Arius.Core.Shared;

namespace Arius.Tests.Shared;

public class RepositoryPathsCleanup
{
    public const string UnitTestRepositoryPrefix = "unittest-";

    [Before(TestSession)]
    public static void CleanupUnitTestRepositoryCachesBeforeSession()
    {
        var root = RepositoryLocalStatePaths.GetAriusRoot().ToString();

        if (!Directory.Exists(root))
            return;

        foreach (var directory in Directory.EnumerateDirectories(root, $"{UnitTestRepositoryPrefix}*", SearchOption.TopDirectoryOnly))
            Directory.Delete(directory, recursive: true);
    }

}