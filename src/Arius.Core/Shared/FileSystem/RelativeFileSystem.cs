namespace Arius.Core.Shared.FileSystem;

internal sealed class RelativeFileSystem
{
    private readonly LocalDirectory _root;

    public RelativeFileSystem(LocalDirectory root)
    {
        _root = root;
    }

    public IEnumerable<LocalFileEntry> EnumerateFiles()
    {
        foreach (var filePath in Directory.EnumerateFiles(_root.ToString(), "*", SearchOption.AllDirectories))
        {
            if (!_root.TryGetRelativePath(filePath, out var relativePath))
                continue;

            var fileInfo = new FileInfo(filePath);
            yield return new LocalFileEntry
            {
                Path = relativePath,
                Size = fileInfo.Length,
                Created = new DateTimeOffset(fileInfo.CreationTimeUtc, TimeSpan.Zero),
                Modified = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero)
            };
        }
    }

    public bool FileExists(RelativePath path) => File.Exists(_root.Resolve(path));

    public bool DirectoryExists(RelativePath path) => Directory.Exists(_root.Resolve(path));

    public Stream OpenRead(RelativePath path) => File.OpenRead(_root.Resolve(path));

    public Stream CreateFile(RelativePath path)
    {
        var fullPath = _root.Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
    }

    public string ReadAllText(RelativePath path) => File.ReadAllText(_root.Resolve(path));

    public Task<string> ReadAllTextAsync(RelativePath path, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(_root.Resolve(path), cancellationToken);

    public async Task WriteAllTextAsync(RelativePath path, string content, CancellationToken cancellationToken)
    {
        var fullPath = _root.Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
    }

    public void CreateDirectory(RelativePath path) => Directory.CreateDirectory(_root.Resolve(path));

    public void DeleteFile(RelativePath path) => File.Delete(_root.Resolve(path));
}
