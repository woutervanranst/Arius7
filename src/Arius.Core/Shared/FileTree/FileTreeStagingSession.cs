using Arius.Core.Shared.Paths;

namespace Arius.Core.Shared.FileTree;

internal interface IFileTreeStagingSession : IAsyncDisposable
{
    LocalRootPath StagingRoot { get; }
}

internal sealed class FileTreeStagingSession : IFileTreeStagingSession
{
    private readonly FileStream _lockStream;

    private FileTreeStagingSession(LocalRootPath stagingRoot, FileStream lockStream)
    {
        StagingRoot = stagingRoot;
        _lockStream = lockStream;
    }

    public LocalRootPath StagingRoot { get; }

    public static Task<FileTreeStagingSession> OpenAsync(string fileTreeCacheDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileTreeCacheDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(fileTreeCacheDirectory);

        var lockPath = FileTreePaths.GetStagingLockPath(fileTreeCacheDirectory);
        FileStream lockStream;

        try
        {
            lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, useAsync: true);
        }
        catch (IOException ex)
        {
            throw new IOException($"Another filetree staging session is already open for '{fileTreeCacheDirectory}'.", ex);
        }

        try
        {
            var stagingRoot = LocalRootPath.Parse(FileTreePaths.GetStagingRootDirectory(fileTreeCacheDirectory));
            if (Directory.Exists(stagingRoot.ToString()))
                Directory.Delete(stagingRoot.ToString(), recursive: true);

            Directory.CreateDirectory(stagingRoot.ToString());
            return Task.FromResult(new FileTreeStagingSession(stagingRoot, lockStream));
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
            if (Directory.Exists(StagingRoot.ToString()))
                Directory.Delete(StagingRoot.ToString(), recursive: true);
        }
        finally
        {
            _lockStream.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
