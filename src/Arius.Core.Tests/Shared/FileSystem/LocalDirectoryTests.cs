using Arius.Tests.Shared;

namespace Arius.Core.Tests.Shared.FileSystem;

public class LocalDirectoryTests : IDisposable
{
    private readonly string _root;

    public LocalDirectoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), TestTempRoots.FolderName, $"local-directory-{Guid.NewGuid():N}");
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
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var root = LocalDirectory.Parse(_root + Path.DirectorySeparatorChar.ToString());

        root.ToString().ShouldBe(Path.GetFullPath(_root));
    }

    [Test]
    public void Parse_RelativeDirectory_Throws()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        Should.Throw<FormatException>(() => LocalDirectory.Parse("relative/root"));
    }

    [Test]
    public void TryGetRelativePath_PathUnderRoot_ReturnsRelativePath()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
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
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var directory = LocalDirectory.Parse(_root);
        var outsidePath = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");

        directory.TryGetRelativePath(outsidePath, out _).ShouldBeFalse();
    }

    [Test]
    public void TryGetRelativePath_RootPath_ReturnsRootRelativePath()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var directory = LocalDirectory.Parse(_root);

        directory.TryGetRelativePath(_root, out var relativePath).ShouldBeTrue();
        relativePath.ShouldBe(RelativePath.Root);
    }

    [Test]
    public void TryGetRelativePath_FileSystemRootDescendant_ReturnsRelativePath()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var rootPath = Path.GetPathRoot(_root)!;
        var directory = LocalDirectory.Parse(rootPath);
        var childPath = Path.Combine(rootPath, $"arius-local-directory-{Guid.NewGuid():N}.txt");

        directory.TryGetRelativePath(childPath, out var relativePath).ShouldBeTrue();
        relativePath.ShouldBe(RelativePath.Parse(Path.GetFileName(childPath)));
    }

    [Test]
    public void TryGetRelativePath_RootDescendant_Resolve_RoundTrips()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var rootPath = Path.GetPathRoot(_root)!;
        var directory = LocalDirectory.Parse(rootPath);
        var relativePath = RelativePath.Parse($"arius-local-directory-{Guid.NewGuid():N}.txt");

        directory.Resolve(relativePath).ShouldBe(Path.Combine(rootPath, relativePath.ToString().Replace('/', Path.DirectorySeparatorChar)));
    }

    [Test]
    public void TryGetRelativePath_CaseDifferenceOnWindows_ReturnsRelativePath()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
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
