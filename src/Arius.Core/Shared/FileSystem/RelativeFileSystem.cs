namespace Arius.Core.Shared.FileSystem;

/// <summary>
/// Provides Arius.Core's local filesystem boundary for work scoped to a <see cref="LocalDirectory"/> root.
/// It exists so features can perform file IO with <see cref="RelativePath"/> values instead of raw strings,
/// with responsibility for containment-safe enumeration, reads, writes, and directory creation.
/// </summary>
[SharedWithinAssembly]
internal sealed class RelativeFileSystem(LocalDirectory root)
{
    // --- FILE EXIST

    public bool FileExists(RelativePath path) 
        => File.Exists(root.Resolve(path));

    // --- DIRECTORY EXIST

    public bool DirectoryExists(RelativePath path) 
        => DirectoryExists(root, path);

    public bool DirectoryExists(LocalDirectory directory) 
        => DirectoryExists(root, directory);

    public static bool DirectoryExists(LocalDirectory root, RelativePath path)
        => Directory.Exists(root.Resolve(path));

    public static bool DirectoryExists(LocalDirectory root, LocalDirectory directory)
    {
        EnsureContained(root, directory);
        return Directory.Exists(directory.ToString());
    }

    // -- DIRECTORY CREATE

    public void CreateDirectory(RelativePath path)
        => CreateDirectory(root, path);

    public void CreateDirectory(LocalDirectory directory)
        => CreateDirectory(root, directory);

    public static void CreateDirectory(LocalDirectory root, RelativePath path)
        => Directory.CreateDirectory(root.Resolve(path));

    public static void CreateDirectory(LocalDirectory root, LocalDirectory directory)
    {
        EnsureContained(root, directory);
        Directory.CreateDirectory(directory.ToString());
    }

    // -- DELETE

    // --- FILE DELETE

    public void DeleteFile(RelativePath path) => File.Delete(root.Resolve(path));

    // --- DIRECTORY DELETE

    public void DeleteDirectory(RelativePath path, bool recursive)
        => DeleteDirectory(root, path, recursive);

    public void DeleteDirectory(LocalDirectory directory, bool recursive)
        => DeleteDirectory(root, directory, recursive);

    public static void DeleteDirectory(LocalDirectory root, RelativePath path, bool recursive) 
        => Directory.Delete(root.Resolve(path), recursive);

    public static void DeleteDirectory(LocalDirectory root, LocalDirectory directory, bool recursive)
    {
        EnsureContained(root, directory);
        Directory.Delete(directory.ToString(), recursive);
    }

    // --- DIRECTORY DELETE IF EXISTS

    public void DeleteDirectoryIfExists(RelativePath path, bool recursive)
    {
        if (DirectoryExists(path))
            DeleteDirectory(path, recursive);
    }

    public void DeleteDirectoryIfExists(LocalDirectory directory, bool recursive)
    {
        if (DirectoryExists(directory))
            DeleteDirectory(root, directory, recursive);
    }

    public static void DeleteDirectoryIfExists(LocalDirectory root, RelativePath path, bool recursive)
    {
        if (DirectoryExists(root, path))
            DeleteDirectory(root, path, recursive);
    }

    public static void DeleteDirectoryIfExists(LocalDirectory root, LocalDirectory directory, bool recursive)
    {
        if (DirectoryExists(root, directory))
            DeleteDirectory(root, directory, recursive);
    }

    // --- DIRECTORY DELETE FILES IN DIRECTORY

    /// <summary>
    /// Deletes all (nested) files in the directory
    /// </summary>
    /// <param name="path"></param>
    public void ClearDirectory(RelativePath path)
    {
        var fullPath = root.Resolve(path);
        if (!Directory.Exists(fullPath))
            return;

        foreach (var filePath in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
            File.Delete(filePath);
    }

    
    // -- ENUMERATE FILES & DIRECTORIES ---

    /// <summary>
    /// Enumerates immediate child directories of the provided relative path.
    /// </summary>
    public IEnumerable<LocalDirectoryEntry> EnumerateDirectories(RelativePath path)
    {
        foreach (var directoryPath in Directory.EnumerateDirectories(root.Resolve(path), "*", SearchOption.TopDirectoryOnly))
        {
            if (!root.TryGetRelativePath(directoryPath, out var relativePath))
                continue;

            yield return new LocalDirectoryEntry { Path = relativePath };
        }
    }

    /// <summary>
    /// Enumerates all files recursively under the rooted directory as repository-relative entries.
    /// </summary>
    public IEnumerable<LocalFileEntry> EnumerateFiles() => EnumerateFiles(RelativePath.Root, SearchOption.AllDirectories);

    /// <summary>
    /// Enumerates immediate child files of the provided relative path.
    /// </summary>
    public IEnumerable<LocalFileEntry> EnumerateFiles(RelativePath path, SearchOption searchOption)
    {
        foreach (var filePath in Directory.EnumerateFiles(root.Resolve(path), "*", searchOption))
        {
            if (!root.TryGetRelativePath(filePath, out var relativePath))
                continue;

            yield return new LocalFileEntry
            {
                Path = relativePath
            };
        }
    }

    public IEnumerable<PathSegment> EnumerateFileNames(RelativePath path)
    {
        var fullPath = root.Resolve(path);
        if (!Directory.Exists(fullPath))
            yield break;

        foreach (var filePath in Directory.EnumerateFiles(fullPath, "*", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var fileName = Path.GetFileName(filePath);
            if (PathSegment.TryParse(fileName, out var segment))
                yield return segment;
        }
    }

    // --- FILE OPERATIONS

    /// <summary>
    /// Creates or overwrites a file within the rooted directory, creating parent directories as needed.
    /// </summary>
    public Stream CreateFile(RelativePath path)
    {
        var fullPath = root.Resolve(path);
        CreateDirectory(path.Parent ?? RelativePath.Root);
        return new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
    }

    /// <summary>
    /// Opens a file for reading within the rooted directory.
    /// </summary>
    public Stream OpenRead(RelativePath path) =>
        new FileStream(root.Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);

    public FileStream OpenOrCreateFile(RelativePath path, FileAccess access, FileShare share, int bufferSize = 4096, bool useAsync = true)
    {
        var fullPath = root.Resolve(path);
        CreateDirectory(path.Parent ?? RelativePath.Root);
        return new FileStream(fullPath, FileMode.OpenOrCreate, access, share, bufferSize, useAsync);
    }

    public string ReadAllText(RelativePath path) => File.ReadAllText(root.Resolve(path));

    public Task<string> ReadAllTextAsync(RelativePath path, CancellationToken cancellationToken) => File.ReadAllTextAsync(root.Resolve(path), cancellationToken);

    public byte[] ReadAllBytes(RelativePath path) => File.ReadAllBytes(root.Resolve(path));

    public Task<byte[]> ReadAllBytesAsync(RelativePath path, CancellationToken cancellationToken) => File.ReadAllBytesAsync(root.Resolve(path), cancellationToken);

    public IEnumerable<string> ReadLines(RelativePath path)
    {
        foreach (var line in File.ReadLines(root.Resolve(path)))
            yield return line;
    }

    public async IAsyncEnumerable<string> ReadLinesAsync(RelativePath path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in File.ReadLinesAsync(root.Resolve(path), cancellationToken))
            yield return line;
    }

    public void WriteAllText(RelativePath path, string content)
    {
        var fullPath = root.Resolve(path);
        CreateDirectory(path.Parent ?? RelativePath.Root);
        File.WriteAllText(fullPath, content);
    }

    public async Task WriteAllTextAsync(RelativePath path, string content, CancellationToken cancellationToken)
    {
        var fullPath = root.Resolve(path);
        CreateDirectory(path.Parent ?? RelativePath.Root);
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
    }

    public void WriteAllBytes(RelativePath path, byte[] content)
    {
        var fullPath = root.Resolve(path);
        CreateDirectory(path.Parent ?? RelativePath.Root);
        File.WriteAllBytes(fullPath, content);
    }

    public async Task WriteAllBytesAsync(RelativePath path, byte[] content, CancellationToken cancellationToken)
    {
        var fullPath = root.Resolve(path);
        CreateDirectory(path.Parent ?? RelativePath.Root);
        await File.WriteAllBytesAsync(fullPath, content, cancellationToken);
    }

    public void ReplaceFileAtomically(RelativePath source, RelativePath destination)
    {
        var sourcePath = root.Resolve(source);
        var destinationPath = root.Resolve(destination);
        CreateDirectory(destination.Parent ?? RelativePath.Root);

        if (OperatingSystem.IsWindows() && File.Exists(destinationPath))
            File.Replace(sourcePath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(sourcePath, destinationPath, overwrite: true);
    }

    /// <summary>
    /// Copies a file within the rooted directory, creating the destination parent directory when needed.
    /// </summary>
    public void CopyFile(RelativePath source, RelativePath destination, bool overwrite)
    {
        var destinationPath = root.Resolve(destination);
        CreateDirectory(destination.Parent ?? RelativePath.Root);
        File.Copy(root.Resolve(source), destinationPath, overwrite);
    }

    public long GetFileSize(RelativePath path) => new FileInfo(root.Resolve(path)).Length;

    /// <summary>
    /// Reads creation and last-write timestamps for a file within the rooted directory.
    /// </summary>
    public (DateTimeOffset Created, DateTimeOffset Modified) GetTimestamps(RelativePath path)
    {
        var fullPath = root.Resolve(path);
        using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        return (
            new DateTimeOffset(File.GetCreationTimeUtc(handle), TimeSpan.Zero),
            new DateTimeOffset(File.GetLastWriteTimeUtc(handle), TimeSpan.Zero));
    }

    /// <summary>
    /// Sets creation and last-write timestamps for a file within the rooted directory.
    /// </summary>
    public void SetTimestamps(RelativePath path, DateTimeOffset created, DateTimeOffset modified)
    {
        var fullPath = root.Resolve(path);
        using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        File.SetCreationTimeUtc(handle, created.UtcDateTime);
        File.SetLastWriteTimeUtc(handle, modified.UtcDateTime);
    }


    // -- HELPERS

    private static void EnsureContained(LocalDirectory root, LocalDirectory directory)
    {
        if (!root.TryGetRelativePath(directory, out _))
            throw new ArgumentException($"Directory '{directory}' is not contained within root '{root}'.", nameof(directory));
    }
}
