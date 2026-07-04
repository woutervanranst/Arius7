using Arius.Core.Shared;

namespace Arius.Core.Tests.Shared;

public class RepositoryLocalStatePathsTests
{
    [Test]
    public void GetHashCacheRoot_IsUnderRepositoryRoot_NamedHash()
    {
        var root = RepositoryLocalStatePaths.GetHashCacheRoot("acct", "cont");
        root.ToString().Replace('\\', '/').ShouldEndWith(".arius/acct-cont/hash");
    }
}
