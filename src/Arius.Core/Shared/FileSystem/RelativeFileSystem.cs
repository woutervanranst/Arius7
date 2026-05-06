namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Provides Arius.Core's local filesystem boundary for work scoped to a <see cref="LocalDirectory"/> root.
/// It exists so features can perform file IO with <see cref="RelativePath"/> values instead of raw strings,
/// with responsibility for containment-safe enumeration, reads, writes, and directory creation.
/// </summary>
internal sealed class RelativeFileSystem
{
    private readonly LocalDirectory _root;

    public RelativeFileSystem(LocalDirectory root)
    {
        _root = root;
    }

    /// <summary>
    /// Enumerates all files under the rooted directory as repository-relative entries.
    /// </summary>
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

    /// <summary>
    /// Enumerates immediate child directories of the provided relative path.
    /// </summary>
    public IEnumerable<LocalDirectoryEntry> EnumerateDirectories(RelativePath path)
    {
        foreach (var directoryPath in Directory.EnumerateDirectories(_root.Resolve(path), "*", SearchOption.TopDirectoryOnly))
        {
            if (!_root.TryGetRelativePath(directoryPath, out var relativePath))
                continue;

            yield return new LocalDirectoryEntry { Path = relativePath };
        }
    }

    /// <summary>
    /// Enumerates immediate child files of the provided relative path.
    /// </summary>
    public IEnumerable<LocalFileEntry> EnumerateFiles(RelativePath path)
    {
        foreach (var filePath in Directory.EnumerateFiles(_root.Resolve(path), "*", SearchOption.TopDirectoryOnly))
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

    /// <summary>
    /// Opens a file for reading within the rooted directory.
    /// </summary>
    public Stream OpenRead(RelativePath path) => File.OpenRead(_root.Resolve(path));

    /// <summary>
    /// Creates or overwrites a file within the rooted directory, creating parent directories as needed.
    /// </summary>
    public Stream CreateFile(RelativePath path)
    {
        var fullPath = _root.Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
    }

    public string ReadAllText(RelativePath path) => File.ReadAllText(_root.Resolve(path));

    public Task<string> ReadAllTextAsync(RelativePath path, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(_root.Resolve(path), cancellationToken);

    public Task<byte[]> ReadAllBytesAsync(RelativePath path, CancellationToken cancellationToken) =>
        File.ReadAllBytesAsync(_root.Resolve(path), cancellationToken);

    public async Task WriteAllTextAsync(RelativePath path, string content, CancellationToken cancellationToken)
    {
        var fullPath = _root.Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
    }

    public async Task WriteAllBytesAsync(RelativePath path, byte[] content, CancellationToken cancellationToken)
    {
        var fullPath = _root.Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, content, cancellationToken);
    }

    public void CreateDirectory(RelativePath path) => Directory.CreateDirectory(_root.Resolve(path));

    public long GetFileSize(RelativePath path) => new FileInfo(_root.Resolve(path)).Length;

    /// <summary>
    /// Sets creation and last-write timestamps for a file within the rooted directory.
    /// </summary>
    public void SetTimestamps(RelativePath path, DateTimeOffset created, DateTimeOffset modified)
    {
        var fullPath = _root.Resolve(path);
        File.SetCreationTimeUtc(fullPath, created.UtcDateTime);
        File.SetLastWriteTimeUtc(fullPath, modified.UtcDateTime);
    }

    /// <summary>
    /// Copies a file within the rooted directory, creating the destination parent directory when needed.
    /// </summary>
    public void CopyFile(RelativePath source, RelativePath destination, bool overwrite)
    {
        var destinationPath = _root.Resolve(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(_root.Resolve(source), destinationPath, overwrite);
    }

    public void DeleteFile(RelativePath path) => File.Delete(_root.Resolve(path));
}
