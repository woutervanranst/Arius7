using Arius.Core.Shared.Paths;

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
    private static readonly string _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string GetRepositoryDirectoryName(string accountName, string containerName) => $"{accountName}-{containerName}";


    /// <summary>
    /// Returns the repository root directory under the user's <c>.arius</c> home.
    /// Example: <c>~/.arius/<container>/</c>
    /// </summary>
    public static LocalRootPath GetRepositoryDirectory(string accountName, string containerName) 
        => LocalRootPath.Parse(Path.Combine(_home, ".arius", GetRepositoryDirectoryName(accountName, containerName)));

    /// <summary>
    /// Returns the chunk-index cache directory for one repository.
    /// Example: <c>~/.arius/<container>/chunk-index/</c>
    /// </summary>
    public static LocalRootPath GetChunkIndexCacheDirectory(string accountName, string containerName) 
        => LocalRootPath.Parse(Path.Combine(GetRepositoryDirectory(accountName, containerName).ToString(), "chunk-index"));

    /// <summary>
    /// Returns the filetree cache directory for one repository.
    /// Example: <c>~/.arius/<container>/filetrees/</c>
    /// </summary>
    public static LocalRootPath GetFileTreeCacheDirectory(string accountName, string containerName) 
        => LocalRootPath.Parse(Path.Combine(GetRepositoryDirectory(accountName, containerName).ToString(), "filetrees"));

    /// <summary>
    /// Returns the snapshot cache directory for one repository.
    /// Example: <c>~/.arius/<container>/snapshots/</c>
    /// </summary>
    public static LocalRootPath GetSnapshotCacheDirectory(string accountName, string containerName) 
        => LocalRootPath.Parse(Path.Combine(GetRepositoryDirectory(accountName, containerName).ToString(), "snapshots"));

    /// <summary>
    /// Returns the logs directory for one repository.
    /// Example: <c>~/.arius/<container>/logs/</c>
    /// </summary>
    public static LocalRootPath GetLogsDirectory(string accountName, string containerName) 
        => LocalRootPath.Parse(Path.Combine(GetRepositoryDirectory(accountName, containerName).ToString(), "logs"));
}
