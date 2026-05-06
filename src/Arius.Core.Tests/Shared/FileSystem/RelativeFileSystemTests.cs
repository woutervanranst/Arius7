using Arius.Core.Shared.FileSystem;

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
    public void OpenRead_MissingFile_Throws()
    {
        Should.Throw<FileNotFoundException>(() => _fileSystem.OpenRead(RelativePath.Parse("missing.bin")));
    }
}
