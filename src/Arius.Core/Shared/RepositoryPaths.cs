namespace Arius.Core.Shared;

/// <summary>
/// Builds repository-level cache roots under the user's <c>.arius</c> home.
///
/// This helper owns the top-level on-disk layout for one repository identity
/// (<c>accountName</c> + <c>containerName</c>).
///
/// Example repository root:
/// <c>~/.arius/<container>/</c>
/// </summary>
public static class RepositoryPaths
{
    /// <summary>
    /// Returns the stable repository directory name for one archive/container pair.
    /// Example: <c>archive-container</c>
    /// </summary>
    public static string GetRepoDirectoryName(string accountName, string containerName) => $"{accountName}-{containerName}";

    /// <summary>
    /// Returns the repository root directory under the user's <c>.arius</c> home.
    /// Example: <c>~/.arius/<container>/</c>
    /// </summary>
    public static string GetRepositoryDirectory(string accountName, string containerName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".arius", GetRepoDirectoryName(accountName, containerName));
    }

    /// <summary>
    /// Returns the chunk-index cache directory for one repository.
    /// Example: <c>~/.arius/<container>/chunk-index/</c>
    /// </summary>
    public static string GetChunkIndexCacheDirectory(string accountName, string containerName) =>
        Path.Combine(GetRepositoryDirectory(accountName, containerName), "chunk-index");

    /// <summary>
    /// Returns the filetree cache directory for one repository.
    /// Example: <c>~/.arius/<container>/filetrees/</c>
    /// </summary>
    public static string GetFileTreeCacheDirectory(string accountName, string containerName) =>
        Path.Combine(GetRepositoryDirectory(accountName, containerName), "filetrees");

    /// <summary>
    /// Returns the snapshot cache directory for one repository.
    /// Example: <c>~/.arius/<container>/snapshots/</c>
    /// </summary>
    public static string GetSnapshotCacheDirectory(string accountName, string containerName) =>
        Path.Combine(GetRepositoryDirectory(accountName, containerName), "snapshots");

    /// <summary>
    /// Returns the logs directory for one repository.
    /// Example: <c>~/.arius/<container>/logs/</c>
    /// </summary>
    public static string GetLogsDirectory(string accountName, string containerName) =>
        Path.Combine(GetRepositoryDirectory(accountName, containerName), "logs");
}
