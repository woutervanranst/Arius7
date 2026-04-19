using Arius.E2E.Tests.Fixtures;

namespace Arius.E2E.Tests;

public class E2EFixturePathTests
{
    [Test]
    public void CombineValidatedRelativePath_AllowsPathInsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"arius-e2e-path-{Guid.NewGuid():N}");

        var resolved = E2EFixture.CombineValidatedRelativePath(root, "nested/file.bin");

        resolved.ShouldBe(Path.Combine(root, "nested", "file.bin"));
    }

    [Test]
    public void CombineValidatedRelativePath_RejectsDotDotTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"arius-e2e-path-{Guid.NewGuid():N}");

        Should.Throw<ArgumentException>(() => E2EFixture.CombineValidatedRelativePath(root, "../escape.bin"));
    }

    [Test]
    public void CombineValidatedRelativePath_RejectsRootedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"arius-e2e-path-{Guid.NewGuid():N}");
        var rooted = Path.GetPathRoot(root) is { Length: > 0 } pathRoot
            ? Path.Combine(pathRoot, "escape.bin")
            : "/escape.bin";

        Should.Throw<ArgumentException>(() => E2EFixture.CombineValidatedRelativePath(root, rooted));
    }
}
