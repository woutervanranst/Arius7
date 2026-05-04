using Arius.Core.Shared.Paths;

namespace Arius.Core.Tests.Shared;

public class RootedPathTests
{
    [Test]
    public void RootedAt_ComposesRootAndRelativePath()
    {
        var root = LocalRootPath.Parse(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arius-rooted")));
        var path = RelativePath.Parse("photos/2024/a.jpg");

        var rooted = path.RootedAt(root);

        rooted.Root.ShouldBe(root);
        rooted.RelativePath.ShouldBe(path);
        rooted.FullPath.ShouldBe(Path.Combine(root.ToString(), "photos", "2024", "a.jpg"));
    }

    [Test]
    public void GetRelativePath_RoundTripsAbsolutePathUnderRoot()
    {
        var root = LocalRootPath.Parse(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arius-roundtrip")));
        var fullPath = Path.Combine(root.ToString(), "docs", "readme.txt");

        root.GetRelativePath(fullPath).ShouldBe(RelativePath.Parse("docs/readme.txt"));
    }
}
