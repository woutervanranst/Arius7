namespace Arius.Core.Shared.FileTree;

internal sealed class FileTreeStagingSession : IAsyncDisposable
{
    private readonly FileStream _lockStream;

    private FileTreeStagingSession(string stagingRoot, FileStream lockStream)
    {
        StagingRoot = stagingRoot;
        _lockStream = lockStream;
    }

    public string StagingRoot { get; }

    public static Task<FileTreeStagingSession> OpenAsync(string fileTreeCacheDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileTreeCacheDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(fileTreeCacheDirectory);

        var lockPath = FileTreeStagingPaths.GetLockPath(fileTreeCacheDirectory);
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
            var stagingRoot = FileTreeStagingPaths.GetStagingRoot(fileTreeCacheDirectory);
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);

            Directory.CreateDirectory(stagingRoot);
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
        _lockStream.Dispose();

        if (Directory.Exists(StagingRoot))
            Directory.Delete(StagingRoot, recursive: true);

        return ValueTask.CompletedTask;
    }
}
