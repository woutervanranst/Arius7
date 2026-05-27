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
    private static readonly RelativePath chunkIndexCacheRelativePath = RelativePath.Parse("chunk-index");
    private static readonly RelativePath fileTreeCacheRelativePath   = RelativePath.Parse("filetrees");
    private static readonly RelativePath snapshotCacheRelativePath   = RelativePath.Parse("snapshots");
    private static readonly RelativePath logsRelativePath            = RelativePath.Parse("logs");

    /// <summary>
    /// Returns the stable repository directory name for one archive/container pair.
    /// Example: <c>archive-container</c>
    /// </summary>
    public static PathSegment GetRepositoryDirectoryName(string accountName, string containerName) 
        => PathSegment.Parse($"{accountName}-{containerName}");

    internal static PathSegment GetRepoDirectoryName(string accountName, string containerName) 
        => GetRepositoryDirectoryName(accountName, containerName);

    internal static LocalDirectory GetRoot() 
        => LocalDirectory.Parse(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static LocalDirectory GetRepositoryLocalPersistentTempRoot(string accountName, string containerName) 
        => GetRoot() / PathSegment.Parse(".arius") / GetRepoDirectoryName(accountName, containerName);

    internal static LocalDirectory GetChunkIndexCacheRoot(string accountName, string containerName) 
        => LocalDirectory.Parse(GetRepositoryLocalPersistentTempRoot(accountName, containerName).Resolve(chunkIndexCacheRelativePath));

    internal static LocalDirectory GetFileTreeCacheRoot(string accountName, string containerName) 
        => LocalDirectory.Parse(GetRepositoryLocalPersistentTempRoot(accountName, containerName).Resolve(fileTreeCacheRelativePath));

    internal static LocalDirectory GetSnapshotCacheRoot(string accountName, string containerName) 
        => LocalDirectory.Parse(GetRepositoryLocalPersistentTempRoot(accountName, containerName).Resolve(snapshotCacheRelativePath));

    internal static LocalDirectory GetLogsRoot(string accountName, string containerName) 
        => LocalDirectory.Parse(GetRepositoryLocalPersistentTempRoot(accountName, containerName).Resolve(logsRelativePath));
}
