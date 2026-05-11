namespace Arius.Core.Tests.Shared.FileSystem;

public class RelativeFileSystemTests : IDisposable
{
    private readonly string _root;
    private readonly RelativeFileSystem _fileSystem;

    public RelativeFileSystemTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"arius-relative-fs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _fileSystem = new RelativeFileSystem(LocalDirectory.Parse(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void EnumerateFiles_StripsRootIntoRelativePath()
    {
        var fullPath = Path.Combine(_root, "photos", "2024", "pic.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "hello");

        var entry = _fileSystem.EnumerateFiles().Single();

        entry.Path.ShouldBe(RelativePath.Parse("photos/2024/pic.jpg"));
    }

    [Test]
    public async Task FileOperations_UseRelativePathsOnly()
    {
        var path = RelativePath.Parse("docs/report.txt");

        await _fileSystem.WriteAllTextAsync(path, "report", CancellationToken.None);
        _fileSystem.FileExists(path).ShouldBeTrue();
        _fileSystem.DirectoryExists(RelativePath.Parse("docs")).ShouldBeTrue();
        (await _fileSystem.ReadAllTextAsync(path, CancellationToken.None)).ShouldBe("report");

        _fileSystem.DeleteFile(path);
        _fileSystem.FileExists(path).ShouldBeFalse();
    }

    [Test]
    public async Task CreateFile_CreatesParentDirectories()
    {
        var path = RelativePath.Parse("deep/nested/file.bin");

        await using (var stream = _fileSystem.CreateFile(path))
        {
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync("payload");
        }

        File.ReadAllText(Path.Combine(_root, "deep", "nested", "file.bin")).ShouldBe("payload");
    }

    [Test]
    public async Task ByteOperations_UseRelativePathsOnly()
    {
        var path = RelativePath.Parse("cache/tree.bin");
        var bytes = new byte[] { 1, 2, 3, 4 };

        await _fileSystem.WriteAllBytesAsync(path, bytes, CancellationToken.None);

        (await _fileSystem.ReadAllBytesAsync(path, CancellationToken.None)).ShouldBe(bytes);
    }

    [Test]
    public async Task GetTimestamps_ReturnsCreationAndLastWriteTimesForRelativePath()
    {
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
        var path = RelativePath.Parse("missing.bin");

        var expected = Should.Throw<FileNotFoundException>(() => _fileSystem.OpenRead(path));
        var actual = Should.Throw<FileNotFoundException>(() => _fileSystem.GetTimestamps(path));

        actual.FileName.ShouldBe(expected.FileName);
    }

    [Test]
    public void OpenRead_MissingFile_Throws()
    {
        Should.Throw<FileNotFoundException>(() => _fileSystem.OpenRead(RelativePath.Parse("missing.bin")));
    }

    [Test]
    public void EnumerateFileNames_ReturnsImmediateChildNamesOnly()
    {
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
        var names = _fileSystem.EnumerateFileNames(RelativePath.Parse("missing")).ToArray();

        names.ShouldBeEmpty();
    }

    [Test]
    public async Task ReplaceFileAtomicallyAsync_PublishesTempFileToDestination()
    {
        var destination = RelativePath.Parse("cache/tree.bin");
        var temp = RelativePath.Parse("cache/.tree.tmp");

        await _fileSystem.WriteAllBytesAsync(temp, [1, 2, 3], CancellationToken.None);

        await _fileSystem.ReplaceFileAtomicallyAsync(temp, destination, CancellationToken.None);

        _fileSystem.FileExists(temp).ShouldBeFalse();
        (await _fileSystem.ReadAllBytesAsync(destination, CancellationToken.None)).ShouldBe([1, 2, 3]);
    }

    [Test]
    public async Task DeleteFilesInDirectory_RemovesOnlyImmediateFiles()
    {
        await _fileSystem.WriteAllTextAsync(RelativePath.Parse("cache/one.txt"), "1", CancellationToken.None);
        await _fileSystem.WriteAllTextAsync(RelativePath.Parse("cache/two.txt"), "2", CancellationToken.None);
        await _fileSystem.WriteAllTextAsync(RelativePath.Parse("cache/nested/three.txt"), "3", CancellationToken.None);

        _fileSystem.DeleteFilesInDirectory(RelativePath.Parse("cache"));

        _fileSystem.FileExists(RelativePath.Parse("cache/one.txt")).ShouldBeFalse();
        _fileSystem.FileExists(RelativePath.Parse("cache/two.txt")).ShouldBeFalse();
        _fileSystem.FileExists(RelativePath.Parse("cache/nested/three.txt")).ShouldBeTrue();
    }
}
