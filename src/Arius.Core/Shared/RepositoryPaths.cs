using Arius.Core.Shared.FileSystem;

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
    internal static RelativePath ChunkIndexCacheRelativePath { get; } = RelativePath.Parse("chunk-index");
    internal static RelativePath FileTreeCacheRelativePath   { get; } = RelativePath.Parse("filetrees");
    internal static RelativePath SnapshotCacheRelativePath   { get; } = RelativePath.Parse("snapshots");
    internal static RelativePath LogsRelativePath            { get; } = RelativePath.Parse("logs");

    /// <summary>
    /// Returns the stable repository directory name for one archive/container pair.
    /// Example: <c>archive-container</c>
    /// </summary>
    public static string GetRepoDirectoryName(string accountName, string containerName) => $"{accountName}-{containerName}";

    internal static LocalDirectory GetRepositoryRoot(string accountName, string containerName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return LocalDirectory.Parse(Path.Combine(home, ".arius", GetRepoDirectoryName(accountName, containerName)));
    }

    internal static LocalDirectory GetChunkIndexCacheRoot(string accountName, string containerName) =>
        LocalDirectory.Parse(Path.Combine(GetRepositoryRoot(accountName, containerName).ToString(), ChunkIndexCacheRelativePath.ToString().Replace('/', Path.DirectorySeparatorChar)));

    internal static LocalDirectory GetFileTreeCacheRoot(string accountName, string containerName) =>
        LocalDirectory.Parse(Path.Combine(GetRepositoryRoot(accountName, containerName).ToString(), FileTreeCacheRelativePath.ToString().Replace('/', Path.DirectorySeparatorChar)));

    internal static LocalDirectory GetSnapshotCacheRoot(string accountName, string containerName) =>
        LocalDirectory.Parse(Path.Combine(GetRepositoryRoot(accountName, containerName).ToString(), SnapshotCacheRelativePath.ToString().Replace('/', Path.DirectorySeparatorChar)));

    internal static LocalDirectory GetLogsRoot(string accountName, string containerName) =>
        LocalDirectory.Parse(Path.Combine(GetRepositoryRoot(accountName, containerName).ToString(), LogsRelativePath.ToString().Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Returns the logs directory for one repository.
    /// Example: <c>~/.arius/<container>/logs/</c>
    /// </summary>
    public static string GetLogsDirectory(string accountName, string containerName) =>
        GetCacheDirectory(accountName, containerName, LogsRelativePath);

    private static string GetCacheDirectory(string accountName, string containerName, RelativePath relativePath) =>
        Path.Combine(GetRepositoryRoot(accountName, containerName).ToString(), relativePath.ToString().Replace('/', Path.DirectorySeparatorChar));
}
