using Arius.Tests.Shared;

namespace Arius.Core.Tests.Shared.FileSystem;

public class LocalDirectoryTests : IDisposable
{
    private readonly LocalDirectory _rootDirectory;
    private readonly RelativeFileSystem _fileSystem;

    public LocalDirectoryTests()
    {
        _rootDirectory = TestTempRoots.CreateDirectory("local-directory");
        _fileSystem = new RelativeFileSystem(_rootDirectory);
        _fileSystem.CreateDirectory(RelativePath.Root);
    }

    public void Dispose()
    {
        _fileSystem.DeleteDirectory(RelativePath.Root, recursive: true);
    }

    [Test]
    public void Parse_AbsoluteDirectory_NormalizesRoot()
    {
        var root = LocalDirectory.Parse(_rootDirectory + Path.DirectorySeparatorChar.ToString());

        root.ToString().ShouldBe(Path.GetFullPath(_rootDirectory.ToString()));
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

    [Test]
    public void TryGetRelativePath_RootPath_ReturnsRootRelativePath()
    {
        var directory = LocalDirectory.Parse(_root);

        directory.TryGetRelativePath(_root, out var relativePath).ShouldBeTrue();
        relativePath.ShouldBe(RelativePath.Root);
    }

    [Test]
    public void TryGetRelativePath_FileSystemRootDescendant_ReturnsRelativePath()
    {
        var rootPath = Path.GetPathRoot(_root)!;
        var directory = LocalDirectory.Parse(rootPath);
        var childPath = Path.Combine(rootPath, $"arius-local-directory-{Guid.NewGuid():N}.txt");

        directory.TryGetRelativePath(childPath, out var relativePath).ShouldBeTrue();
        relativePath.ShouldBe(RelativePath.Parse(Path.GetFileName(childPath)));
    }

    [Test]
    public void TryGetRelativePath_RootDescendant_Resolve_RoundTrips()
    {
        var rootPath = Path.GetPathRoot(_root)!;
        var directory = LocalDirectory.Parse(rootPath);
        var relativePath = RelativePath.Parse($"arius-local-directory-{Guid.NewGuid():N}.txt");

        directory.Resolve(relativePath).ShouldBe(Path.Combine(rootPath, relativePath.ToString().Replace('/', Path.DirectorySeparatorChar)));
    }

    [Test]
    public void TryGetRelativePath_CaseDifferenceOnWindows_ReturnsRelativePath()
    {
        if (Path.DirectorySeparatorChar != '\\')
            return;

        var directory = LocalDirectory.Parse(_root);
        var pathSegments = _root.Split(Path.DirectorySeparatorChar, StringSplitOptions.None);
        var adjustedSegments = pathSegments
            .Select((segment, index) => index == pathSegments.Length - 1 ? segment.ToUpperInvariant() : segment)
            .ToArray();
        var alternateCasedRoot = string.Join(Path.DirectorySeparatorChar, adjustedSegments);
        var filePath = Path.Combine(alternateCasedRoot, "photos", "pic.jpg");

        directory.TryGetRelativePath(filePath, out var relativePath).ShouldBeTrue();
        relativePath.ShouldBe(RelativePath.Parse("photos/pic.jpg"));
    }
}
