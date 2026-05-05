using Arius.Core.Shared.Paths;

namespace Arius.Core.Tests.Shared;

public class LocalRootPathTests
{
    [Test]
    public void Parse_AbsolutePath_CanonicalizesAndDoesNotRequireExistence()
    {
        var path = Path.Combine(Path.GetTempPath(), "arius-root-tests", "..", "arius-root-tests", "source");

        var root = LocalRootPath.Parse(path);

        root.ToString().ShouldBe(Path.GetFullPath(path));
    }

    [Test]
    public void Parse_RelativePath_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => LocalRootPath.Parse("source"));
    }

    [Test]
    public void Equality_FollowsHostPathSemantics()
    {
        var lower = LocalRootPath.Parse(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "arius-host-equality")));
        var upper = LocalRootPath.Parse(lower.ToString().ToUpperInvariant());

        if (OperatingSystem.IsWindows())
        {
            lower.ShouldBe(upper);
        }
        else
        {
            lower.ShouldNotBe(upper);
        }
    }

    [Test]
    public void ExistsDirectory_And_CreateDirectory_WorkAgainstRootDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"arius-local-root-io-{Guid.NewGuid():N}");

        try
        {
            var root = LocalRootPath.Parse(tempRoot);

            root.ExistsDirectory.ShouldBeFalse();

            root.CreateDirectory();

            root.ExistsDirectory.ShouldBeTrue();
            Directory.Exists(tempRoot).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }
}
