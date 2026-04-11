using Arius.Core.Shared;
using Shouldly;

namespace Arius.Core.Tests.Shared;

public class RepositoryPathsCompatibilityTests
{
    [Test]
    public void RepoDirectoryName_Format_IsAccountHyphenContainer()
    {
        var name = RepositoryPaths.GetRepoDirectoryName("mystorageacct", "photos");
        name.ShouldBe("mystorageacct-photos");
    }

    [Test]
    public void RepoDirectoryName_DifferentContainers_ProduceDifferentNames()
    {
        var n1 = RepositoryPaths.GetRepoDirectoryName("account", "container1");
        var n2 = RepositoryPaths.GetRepoDirectoryName("account", "container2");
        n1.ShouldNotBe(n2);
    }

    [Test]
    public void RepoDirectoryName_SameInputs_ProduceSameResult()
    {
        var n1 = RepositoryPaths.GetRepoDirectoryName("account", "container");
        var n2 = RepositoryPaths.GetRepoDirectoryName("account", "container");
        n1.ShouldBe(n2);
    }
}
