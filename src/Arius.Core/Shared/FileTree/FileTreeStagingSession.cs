using Arius.Core.Shared.FileSystem;

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

    public static Task<FileTreeStagingSession> OpenAsync(LocalRootPath fileTreeCacheDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        fileTreeCacheDirectory.CreateDirectory();

        var lockPath = FileTreePaths.GetStagingLockPath(fileTreeCacheDirectory);
        FileStream lockStream;

        try
        {
            lockStream = lockPath.Open(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, useAsync: true);
        }
        catch (IOException ex)
        {
            throw new IOException($"Another filetree staging session is already open for '{fileTreeCacheDirectory}'.", ex);
        }

        try
        {
            var stagingRoot = FileTreePaths.GetStagingRootDirectory(fileTreeCacheDirectory);
            if (stagingRoot.ExistsDirectory)
                stagingRoot.DeleteDirectory(recursive: true);

            stagingRoot.CreateDirectory();
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
            if (StagingRoot.ExistsDirectory)
                StagingRoot.DeleteDirectory(recursive: true);
        }
        finally
        {
            _lockStream.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
