using Arius.Tests.Shared;

namespace Arius.Core.Tests.Shared.FileSystem;

public class RelativeFileSystemTests : IDisposable
{
    private readonly string _root;
    private readonly RelativeFileSystem _fileSystem;
    private readonly List<string> _extraRoots = [];

    public RelativeFileSystemTests()
    {
        _root = Path.Combine(Path.GetTempPath(), TestTempRoots.FolderName, $"relative-fs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _fileSystem = new RelativeFileSystem(LocalDirectory.Parse(_root));
    }

    public void Dispose()
    {
        foreach (var extraRoot in _extraRoots)
        {
            if (Directory.Exists(extraRoot))
                Directory.Delete(extraRoot, recursive: true);
        }

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void EnumerateFiles_StripsRootIntoRelativePath()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var fullPath = Path.Combine(_root, "photos", "2024", "pic.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "hello");

        var entry = _fileSystem.EnumerateFiles().Single();

        entry.Path.ShouldBe(RelativePath.Parse("photos/2024/pic.jpg"));
    }

    [Test]
    public async Task FileOperations_UseRelativePathsOnly()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var path = RelativePath.Parse("docs/report.txt");
        var fullPath = Path.Combine(_root, "docs", "report.txt");

        await _fileSystem.WriteAllTextAsync(path, "report", CancellationToken.None);

        File.Exists(fullPath).ShouldBeTrue();
        Directory.Exists(Path.GetDirectoryName(fullPath)!).ShouldBeTrue();
        _fileSystem.FileExists(path).ShouldBeTrue();
        _fileSystem.DirectoryExists(RelativePath.Parse("docs")).ShouldBeTrue();
        (await _fileSystem.ReadAllTextAsync(path, CancellationToken.None)).ShouldBe("report");
        File.ReadAllText(fullPath).ShouldBe("report");

        _fileSystem.DeleteFile(path);

        File.Exists(fullPath).ShouldBeFalse();
        _fileSystem.FileExists(path).ShouldBeFalse();
    }

    [Test]
    public async Task CreateFile_CreatesParentDirectories()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var path = RelativePath.Parse("deep/nested/file.bin");
        var fullPath = Path.Combine(_root, "deep", "nested", "file.bin");

        await using (var stream = _fileSystem.CreateFile(path))
        {
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync("payload");
        }

        Directory.Exists(Path.Combine(_root, "deep", "nested")).ShouldBeTrue();
        File.ReadAllText(fullPath).ShouldBe("payload");
    }

    [Test]
    public void TestTempRoots_CreateDirectory_PlacesRootUnderTempPathArius()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var tempRoot = TestTempRoots.CreateDirectory("relative-fs-test");
        _extraRoots.Add(tempRoot.ToString());

        tempRoot.ToString().ShouldStartWith(Path.Combine(Path.GetTempPath(), TestTempRoots.FolderName) + Path.DirectorySeparatorChar);
        tempRoot.ToString().ShouldContain("relative-fs-test-");
    }

    [Test]
    public async Task ByteOperations_UseRelativePathsOnly()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var path = RelativePath.Parse("cache/tree.bin");
        var bytes = new byte[] { 1, 2, 3, 4 };
        var fullPath = Path.Combine(_root, "cache", "tree.bin");

        await _fileSystem.WriteAllBytesAsync(path, bytes, CancellationToken.None);

        File.ReadAllBytes(fullPath).ShouldBe(bytes);
        (await _fileSystem.ReadAllBytesAsync(path, CancellationToken.None)).ShouldBe(bytes);
    }

    [Test]
    public void LocalDirectoryOperations_UseRootScopedDirectories()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var cacheDirectoryPath = Path.Combine(_root, "cache");
        var cacheDirectory = LocalDirectory.Parse(cacheDirectoryPath);

        _fileSystem.CreateDirectory(cacheDirectory);

        Directory.Exists(cacheDirectoryPath).ShouldBeTrue();
        _fileSystem.DirectoryExists(cacheDirectory).ShouldBeTrue();

        _fileSystem.DeleteDirectory(cacheDirectory, recursive: true);

        Directory.Exists(cacheDirectoryPath).ShouldBeFalse();
        _fileSystem.DirectoryExists(cacheDirectory).ShouldBeFalse();
    }

    [Test]
    public void LocalDirectoryOperations_OutsideRoot_ThrowArgumentException()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var outsideRoot = TestTempRoots.CreateDirectory("relative-fs-outside");
        _extraRoots.Add(outsideRoot.ToString());

        Should.Throw<ArgumentException>(() => _fileSystem.DirectoryExists(outsideRoot));
        Should.Throw<ArgumentException>(() => _fileSystem.CreateDirectory(outsideRoot));
        Should.Throw<ArgumentException>(() => _fileSystem.DeleteDirectory(outsideRoot, recursive: true));
    }

    [Test]
    public async Task GetTimestamps_ReturnsCreationAndLastWriteTimesForRelativePath()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var path = RelativePath.Parse("docs/report.txt");
        var created = new DateTimeOffset(2024, 6, 15, 10, 20, 30, TimeSpan.Zero);
        var modified = new DateTimeOffset(2025, 7, 16, 11, 21, 31, TimeSpan.Zero);
        var fullPath = Path.Combine(_root, "docs", "report.txt");

        await _fileSystem.WriteAllTextAsync(path, "report", CancellationToken.None);
        _fileSystem.SetTimestamps(path, created, modified);

        var expectedCreated = new DateTimeOffset(File.GetCreationTimeUtc(fullPath), TimeSpan.Zero);
        var expectedModified = new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero);

        var timestamps = _fileSystem.GetTimestamps(path);

        timestamps.Created.ShouldBe(expectedCreated);
        timestamps.Modified.ShouldBe(expectedModified);
    }

    [Test]
    public void GetTimestamps_MissingFile_Throws()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var path = RelativePath.Parse("missing.bin");

        var expected = Should.Throw<FileNotFoundException>(() => _fileSystem.OpenRead(path));
        var actual = Should.Throw<FileNotFoundException>(() => _fileSystem.GetTimestamps(path));

        actual.FileName.ShouldBe(expected.FileName);
    }

    [Test]
    public void OpenRead_MissingFile_Throws()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        Should.Throw<FileNotFoundException>(() => _fileSystem.OpenRead(RelativePath.Parse("missing.bin")));
    }

    [Test]
    public void EnumerateFileNames_ReturnsImmediateChildNamesOnly()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        Directory.CreateDirectory(Path.Combine(_root, "cache", "nested"));
        File.WriteAllText(Path.Combine(_root, "cache", "b.txt"), "b");
        File.WriteAllText(Path.Combine(_root, "cache", "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "cache", "nested", "c.txt"), "c");

        var names = _fileSystem.EnumerateFileNames(RelativePath.Parse("cache")).ToArray();

        names.ShouldBe([PathSegment.Parse("a.txt"), PathSegment.Parse("b.txt")]);
    }

    [Test]
    public void EnumerateFileNames_MissingDirectory_ReturnsEmpty()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var names = _fileSystem.EnumerateFileNames(RelativePath.Parse("missing")).ToArray();

        names.ShouldBeEmpty();
    }

    [Test]
    public async Task ReplaceFileAtomicallyAsync_PublishesTempFileToDestination()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var destination = RelativePath.Parse("cache/tree.bin");
        var temp = RelativePath.Parse("cache/.tree.tmp");
        var tempPath = Path.Combine(_root, "cache", ".tree.tmp");
        var destinationPath = Path.Combine(_root, "cache", "tree.bin");

        await _fileSystem.WriteAllBytesAsync(temp, [1, 2, 3], CancellationToken.None);

        await _fileSystem.ReplaceFileAtomicallyAsync(temp, destination, CancellationToken.None);

        File.Exists(tempPath).ShouldBeFalse();
        File.ReadAllBytes(destinationPath).ShouldBe([1, 2, 3]);
        _fileSystem.FileExists(temp).ShouldBeFalse();
        (await _fileSystem.ReadAllBytesAsync(destination, CancellationToken.None)).ShouldBe([1, 2, 3]);
    }

    [Test]
    public async Task DeleteFilesInDirectory_RemovesOnlyImmediateFiles()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        await _fileSystem.WriteAllTextAsync(RelativePath.Parse("cache/one.txt"), "1", CancellationToken.None);
        await _fileSystem.WriteAllTextAsync(RelativePath.Parse("cache/two.txt"), "2", CancellationToken.None);
        await _fileSystem.WriteAllTextAsync(RelativePath.Parse("cache/nested/three.txt"), "3", CancellationToken.None);

        _fileSystem.DeleteFilesInDirectory(RelativePath.Parse("cache"));

        File.Exists(Path.Combine(_root, "cache", "one.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "cache", "two.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "cache", "nested", "three.txt")).ShouldBeTrue();
    }

    [Test]
    public void EnumerateDirectories_ReturnsImmediateChildDirectoriesOnly()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        Directory.CreateDirectory(Path.Combine(_root, "cache", "a"));
        Directory.CreateDirectory(Path.Combine(_root, "cache", "b"));
        Directory.CreateDirectory(Path.Combine(_root, "cache", "a", "nested"));

        var directories = _fileSystem.EnumerateDirectories(RelativePath.Parse("cache"))
            .Select(entry => entry.Path)
            .ToArray();

        directories.ShouldBe([
            RelativePath.Parse("cache/a"),
            RelativePath.Parse("cache/b")
        ], ignoreOrder: true);
    }

    [Test]
    public void EnumerateFilesRecursively_ReturnsContainedFilesBelowRelativePath()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        Directory.CreateDirectory(Path.Combine(_root, "cache", "a", "nested"));
        Directory.CreateDirectory(Path.Combine(_root, "other"));
        File.WriteAllText(Path.Combine(_root, "cache", "a.txt"), "a");
        File.WriteAllText(Path.Combine(_root, "cache", "a", "nested", "b.txt"), "b");
        File.WriteAllText(Path.Combine(_root, "other", "c.txt"), "c");

        var files = _fileSystem.EnumerateFilesRecursively(RelativePath.Parse("cache"))
            .Select(entry => entry.Path)
            .ToArray();

        files.ShouldBe([
            RelativePath.Parse("cache/a.txt"),
            RelativePath.Parse("cache/a/nested/b.txt")
        ], ignoreOrder: true);
    }

    [Test]
    public void OpenOrCreateFile_UsesTheProvidedModeWithinTheRootedFilesystem()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var path = RelativePath.Parse("cache/tree.bin");
        var fullPath = Path.Combine(_root, "cache", "tree.bin");

        using (var stream = _fileSystem.OpenOrCreateFile(path, FileAccess.ReadWrite, FileShare.None))
        using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            writer.Write("abc");
            writer.Flush();
        }

        File.Exists(fullPath).ShouldBeTrue();
        File.ReadAllText(fullPath).ShouldBe("abc");

        using var reopenedStream = _fileSystem.OpenOrCreateFile(path, FileAccess.ReadWrite, FileShare.None);
        reopenedStream.Length.ShouldBe(3);
    }

    [Test]
    public void CopyFile_CopiesContentsToDestinationAndCreatesMissingParentDirectories()
    {
        // NOTE: These tests are testing the FileSystem abstraction - keep the System.IO.Directory/File/Path types to avoid testing the abstraction against itself
        var source = RelativePath.Parse("source/data.bin");
        var destination = RelativePath.Parse("copies/nested/data.bin");
        var sourcePath = Path.Combine(_root, "source", "data.bin");
        var destinationPath = Path.Combine(_root, "copies", "nested", "data.bin");

        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);

        _fileSystem.CopyFile(source, destination, overwrite: false);

        Directory.Exists(Path.Combine(_root, "copies", "nested")).ShouldBeTrue();
        File.ReadAllBytes(destinationPath).ShouldBe([1, 2, 3, 4]);
        File.ReadAllBytes(sourcePath).ShouldBe([1, 2, 3, 4]);
    }
}
