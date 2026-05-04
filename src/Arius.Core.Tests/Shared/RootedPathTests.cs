using Arius.Core.Shared.Paths;

namespace Arius.Core.Tests.Shared;

public class RootedPathTests
{
    [Test]
    public void RootedAt_ComposesRootAndRelativePath()
    {
        var root = RootOf(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arius-rooted")));
        var path = RelativePath.Parse("photos/2024/a.jpg");

        var rooted = path.RootedAt(root);

        rooted.Root.ShouldBe(root);
        rooted.RelativePath.ShouldBe(path);
        rooted.FullPath.ShouldBe(Path.Combine(root.ToString(), "photos", "2024", "a.jpg"));
    }

    [Test]
    public void SlashOperator_ComposesRootAndRelativePath()
    {
        var root = RootOf(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arius-rooted-operator")));
        var path = PathOf("docs/readme.txt");

        var rooted = root / path;

        rooted.ShouldBe(new RootedPath(root, path));
        rooted.FullPath.ShouldBe(Path.Combine(root.ToString(), "docs", "readme.txt"));
    }

    [Test]
    public void GetRelativePath_RoundTripsAbsolutePathUnderRoot()
    {
        var root = RootOf(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arius-roundtrip")));
        var fullPath = Path.Combine(root.ToString(), "docs", "readme.txt");

        root.GetRelativePath(fullPath).ShouldBe(RelativePath.Parse("docs/readme.txt"));
    }
}
