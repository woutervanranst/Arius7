namespace Arius.Core.Shared.FileTree;

[SharedWithinAssembly]
internal interface IFileTreeStagingSession : IAsyncDisposable
{
    LocalDirectory StagingRoot { get; }
}

[SharedWithinAssembly]
internal sealed class FileTreeStagingSession : IFileTreeStagingSession
{
    private readonly FileStream _lockStream;
    private readonly RelativeFileSystem _cacheFileSystem;
    private static readonly RelativePath StagingRootPath = RelativePath.Parse(".staging");

    private FileTreeStagingSession(LocalDirectory stagingRoot, FileStream lockStream, RelativeFileSystem cacheFileSystem)
    {
        StagingRoot = stagingRoot;
        _lockStream = lockStream;
        _cacheFileSystem = cacheFileSystem;
    }

    public LocalDirectory StagingRoot { get; }

    public static Task<FileTreeStagingSession> OpenAsync(LocalDirectory fileTreeCacheDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheFileSystem = new RelativeFileSystem(fileTreeCacheDirectory);
        cacheFileSystem.CreateDirectory(RelativePath.Root);

        var lockPath = FileTreePaths.GetStagingLockPath();
        FileStream lockStream;

        try
        {
            lockStream = cacheFileSystem.OpenOrCreateFile(lockPath, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, useAsync: true);
        }
        catch (IOException ex)
        {
            throw new IOException($"Another filetree staging session is already open for '{fileTreeCacheDirectory}'.", ex);
        }

        try
        {
            var stagingRoot = FileTreePaths.GetStagingRootDirectory(fileTreeCacheDirectory);
            cacheFileSystem.DeleteDirectoryIfExists(StagingRootPath, recursive: true);

            cacheFileSystem.CreateDirectory(StagingRootPath);
            return Task.FromResult(new FileTreeStagingSession(stagingRoot, lockStream, cacheFileSystem));
        }
        catch
        {
            lockStream.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _cacheFileSystem.DeleteDirectoryIfExists(StagingRootPath, recursive: true);
        }
        finally
        {
            _lockStream.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
