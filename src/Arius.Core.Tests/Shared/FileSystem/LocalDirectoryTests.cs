using Arius.Core.Shared.FileSystem;

namespace Arius.Core.Tests.Shared.FileSystem;

public class LocalDirectoryTests : IDisposable
{
    private readonly string _root;

    public LocalDirectoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"arius-local-directory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void Parse_AbsoluteDirectory_NormalizesRoot()
    {
        var root = LocalDirectory.Parse(_root + Path.DirectorySeparatorChar);

        root.ToString().ShouldBe(Path.GetFullPath(_root));
    }

    [Test]
    public void Parse_RelativeDirectory_Throws()
    {
        Should.Throw<FormatException>(() => LocalDirectory.Parse("relative/root"));
    }

    [Test]
    public void TryGetRelativePath_PathUnderRoot_ReturnsRelativePath()
    {
        var directory = LocalDirectory.Parse(_root);
        var filePath = Path.Combine(_root, "photos", "pic.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "x");

        directory.TryGetRelativePath(filePath, out var relativePath).ShouldBeTrue();
        relativePath.ShouldBe(RelativePath.Parse("photos/pic.jpg"));
    }

    [Test]
    public void TryGetRelativePath_PathOutsideRoot_ReturnsFalse()
    {
        var directory = LocalDirectory.Parse(_root);
        var outsidePath = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");

        directory.TryGetRelativePath(outsidePath, out _).ShouldBeFalse();
    }
}
